using System.Text.Json;
using CAP_Contracts.Logger;

namespace CAP_Core.ComponentRegistry.RegistryClient;

/// <summary>
/// Read-only client for the open photonic component registry
/// (<c>github.com/aignermax/photonic-registry</c>). Downloads the index,
/// component manifests and S-parameter artifacts via HTTPS and mirrors them in
/// a local cache, so all reads work offline once cached. Fetches never throw
/// on network failure — callers inspect <see cref="RegistryResult{T}"/>.
/// </summary>
public class RegistryClient
{
    /// <summary>Raw-content base URL of the public registry repository.</summary>
    public const string DefaultBaseUrl =
        "https://raw.githubusercontent.com/aignermax/photonic-registry/main";

    /// <summary>Repo-relative path of the registry index document.</summary>
    public const string IndexPath = "index.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly RegistryCache _cache;
    private readonly ILogger? _logger;
    private readonly string _baseUrl;

    /// <summary>
    /// Creates a registry client.
    /// </summary>
    /// <param name="httpClient">HTTP client used for downloads (tests inject a stub handler).</param>
    /// <param name="cache">Local document cache; defaults to the per-user app-data cache.</param>
    /// <param name="logger">Optional logger for fetch failures.</param>
    /// <param name="baseUrl">Registry base URL; defaults to the public GitHub registry.</param>
    public RegistryClient(
        HttpClient httpClient,
        RegistryCache? cache = null,
        ILogger? logger = null,
        string baseUrl = DefaultBaseUrl)
    {
        _httpClient = httpClient;
        _cache = cache ?? RegistryCache.CreateDefault();
        _logger = logger;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    /// <summary>
    /// Fetches the registry index listing all processes and components.
    /// Cache-first: a cached copy is returned without touching the network
    /// unless <paramref name="forceRefresh"/> is true.
    /// </summary>
    public Task<RegistryResult<RegistryIndex>> GetIndexAsync(
        bool forceRefresh = false, CancellationToken cancellationToken = default) =>
        FetchAsync<RegistryIndex>(IndexPath, forceRefresh, cancellationToken);

    /// <summary>
    /// Fetches a component manifest.
    /// </summary>
    /// <param name="manifestPath">Repo-relative manifest path from <see cref="RegistryIndexEntry.Path"/>.</param>
    /// <param name="forceRefresh">True to bypass the cache and re-download.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    public Task<RegistryResult<ComponentManifest>> GetComponentAsync(
        string manifestPath, bool forceRefresh = false, CancellationToken cancellationToken = default) =>
        FetchAsync<ComponentManifest>(manifestPath, forceRefresh, cancellationToken);

    /// <summary>
    /// Fetches a sampled S-parameter spectrum artifact.
    /// </summary>
    /// <param name="manifestPath">Repo-relative path of the component manifest the artifact belongs to.</param>
    /// <param name="artifact">Artifact reference from the manifest (its file path is component-relative).</param>
    /// <param name="forceRefresh">True to bypass the cache and re-download.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    public Task<RegistryResult<SParameterSpectrum>> GetSpectrumAsync(
        string manifestPath, ArtifactRef artifact,
        bool forceRefresh = false, CancellationToken cancellationToken = default) =>
        FetchAsync<SParameterSpectrum>(
            ResolveArtifactPath(manifestPath, artifact.File), forceRefresh, cancellationToken);

    /// <summary>
    /// Resolves an artifact file path (relative to the component directory)
    /// against the repo-relative manifest path.
    /// </summary>
    public static string ResolveArtifactPath(string manifestPath, string artifactFile)
    {
        var lastSlash = manifestPath.LastIndexOf('/');
        var componentDirectory = lastSlash < 0 ? "" : manifestPath[..(lastSlash + 1)];
        return componentDirectory + artifactFile.TrimStart('/');
    }

    private async Task<RegistryResult<T>> FetchAsync<T>(
        string registryPath, bool forceRefresh, CancellationToken cancellationToken) where T : class
    {
        if (!forceRefresh && TryReadFromCache<T>(registryPath) is { } cached)
            return cached;

        var (json, networkError) = await DownloadAsync(registryPath, cancellationToken);
        if (json is null)
            return FallBackToCache<T>(registryPath, networkError!);

        return DeserializeDownload<T>(registryPath, json);
    }

    private RegistryResult<T> DeserializeDownload<T>(string registryPath, string json) where T : class
    {
        var value = TryDeserialize<T>(registryPath, json, out var parseError);
        if (value is null)
            return RegistryResult<T>.Failure(parseError!);

        _cache.Write(registryPath, json);
        return RegistryResult<T>.Success(value, RegistrySource.Network);
    }

    private RegistryResult<T>? TryReadFromCache<T>(string registryPath) where T : class
    {
        if (_cache.TryRead(registryPath) is not { } cachedJson)
            return null;

        var value = TryDeserialize<T>(registryPath, cachedJson, out _);
        return value is null
            ? null // Corrupt cache entry: fall through to a fresh download.
            : RegistryResult<T>.Success(value, RegistrySource.Cache);
    }

    private RegistryResult<T> FallBackToCache<T>(string registryPath, string networkError) where T : class
    {
        if (TryReadFromCache<T>(registryPath) is { } cached)
        {
            _logger?.Print($"Registry: serving '{registryPath}' from cache ({networkError})");
            return cached;
        }
        _logger?.PrintErr($"Registry: failed to fetch '{registryPath}' and no cached copy exists: {networkError}");
        return RegistryResult<T>.Failure(networkError);
    }

    private async Task<(string? Json, string? Error)> DownloadAsync(
        string registryPath, CancellationToken cancellationToken)
    {
        var url = $"{_baseUrl}/{registryPath}";
        try
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return (null, $"HTTP {(int)response.StatusCode} for {url}");
            return (await response.Content.ReadAsStringAsync(cancellationToken), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return (null, $"Network error for {url}: {ex.Message}");
        }
    }

    private T? TryDeserialize<T>(string registryPath, string json, out string? error) where T : class
    {
        try
        {
            var value = JsonSerializer.Deserialize<T>(json, JsonOptions);
            error = value is null ? $"Registry document '{registryPath}' deserialized to null." : null;
            return value;
        }
        catch (JsonException ex)
        {
            error = $"Malformed JSON in registry document '{registryPath}': {ex.Message}";
            _logger?.PrintErr(error);
            return null;
        }
    }
}
