using System.Text.Json;

namespace CAP_Core.ComponentRegistry;

/// <summary>
/// Read-only client for the open photonic component registry
/// (https://github.com/aignermax/photonic-registry). Fetches the index,
/// component manifests, and S-parameter artifacts serverlessly via raw
/// GitHub URLs, caching every document locally: cached documents are served
/// without network access, an explicit refresh re-downloads, and network
/// failures degrade to cached data instead of throwing.
/// </summary>
public sealed class RegistryClient
{
    /// <summary>Raw base URL of the reference registry repository.</summary>
    public const string DefaultBaseUrl =
        "https://raw.githubusercontent.com/aignermax/photonic-registry/main/";

    /// <summary>Repo-relative path of the registry index document.</summary>
    public const string IndexPath = "index.json";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly RegistryCache _cache;
    private readonly string _baseUrl;

    /// <summary>
    /// Initialises the client.
    /// </summary>
    /// <param name="httpClient">Transport; inject a mocked handler in tests.</param>
    /// <param name="baseUrl">Registry base URL; null uses <see cref="DefaultBaseUrl"/>.</param>
    /// <param name="cache">Document cache; null uses the default app-data cache.</param>
    public RegistryClient(HttpClient httpClient, string? baseUrl = null, RegistryCache? cache = null)
    {
        _httpClient = httpClient;
        _baseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/') + "/";
        _cache = cache ?? new RegistryCache();
    }

    /// <summary>Fetches the registry index listing all processes and components.</summary>
    /// <param name="forceRefresh">True bypasses the cache and re-downloads.</param>
    public Task<RegistryResult<RegistryIndex>> GetIndexAsync(
        bool forceRefresh = false, CancellationToken cancellationToken = default)
        => FetchJsonAsync<RegistryIndex>(IndexPath, forceRefresh, cancellationToken);

    /// <summary>
    /// Fetches a component manifest.
    /// </summary>
    /// <param name="componentPath">Repo-relative manifest path as listed in the index
    /// (e.g. "processes/generic-si220/components/y-branch-1x2/component.json").</param>
    /// <param name="forceRefresh">True bypasses the cache and re-downloads.</param>
    public Task<RegistryResult<RegistryComponent>> GetComponentAsync(
        string componentPath, bool forceRefresh = false, CancellationToken cancellationToken = default)
        => FetchJsonAsync<RegistryComponent>(componentPath, forceRefresh, cancellationToken);

    /// <summary>
    /// Fetches an S-parameter artifact of a component.
    /// </summary>
    /// <param name="componentPath">Repo-relative manifest path of the owning component.</param>
    /// <param name="artifactFile">Artifact path relative to the component directory,
    /// as given by <see cref="RegistryArtifactRef.File"/>.</param>
    /// <param name="forceRefresh">True bypasses the cache and re-downloads.</param>
    public Task<RegistryResult<SParameterSpectrum>> GetSpectrumAsync(
        string componentPath, string artifactFile, bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var componentDirectory = componentPath.Contains('/')
            ? componentPath[..componentPath.LastIndexOf('/')]
            : "";
        var artifactPath = string.IsNullOrEmpty(componentDirectory)
            ? artifactFile
            : $"{componentDirectory}/{artifactFile}";
        return FetchJsonAsync<SParameterSpectrum>(artifactPath, forceRefresh, cancellationToken);
    }

    private async Task<RegistryResult<T>> FetchJsonAsync<T>(
        string relativePath, bool forceRefresh, CancellationToken cancellationToken) where T : class
    {
        if (!forceRefresh)
        {
            var cached = TryReadCache<T>(relativePath);
            if (cached != null)
                return RegistryResult<T>.FromCache(cached);
        }

        string? downloadError;
        try
        {
            var json = await _httpClient.GetStringAsync(_baseUrl + relativePath, cancellationToken);
            var value = JsonSerializer.Deserialize<T>(json, JsonOptions);
            if (value != null)
            {
                _cache.Write(relativePath, json);
                return RegistryResult<T>.FromNetwork(value);
            }
            downloadError = $"Registry document '{relativePath}' deserialized to null.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            downloadError = $"Failed to download registry document '{relativePath}': {ex.Message}";
        }

        var fallback = TryReadCache<T>(relativePath);
        return fallback != null
            ? RegistryResult<T>.FromCache(fallback, downloadError)
            : RegistryResult<T>.Unavailable(downloadError);
    }

    /// <summary>Deserializes the cached document, treating corrupt cache entries as cache misses.</summary>
    private T? TryReadCache<T>(string relativePath) where T : class
    {
        var json = _cache.Read(relativePath);
        if (json == null)
            return null;
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
