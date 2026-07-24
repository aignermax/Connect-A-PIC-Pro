using CAP_Core.Helpers;

namespace CAP_Core.ComponentRegistry.RegistryClient;

/// <summary>
/// File-based local cache for downloaded registry documents. Documents are
/// stored under the per-user application-data folder, mirroring their
/// repo-relative paths, so the client keeps working offline once populated.
/// </summary>
public class RegistryCache
{
    private readonly string _rootDirectory;

    /// <summary>
    /// Creates a cache rooted at <paramref name="rootDirectory"/>.
    /// Use <see cref="CreateDefault"/> for the standard per-user location.
    /// </summary>
    public RegistryCache(string rootDirectory)
    {
        _rootDirectory = rootDirectory;
    }

    /// <summary>
    /// Creates the default cache under
    /// <c>&lt;LocalApplicationData&gt;/Lunima/ComponentRegistry</c> (cross-platform).
    /// </summary>
    public static RegistryCache CreateDefault() => new(
        Path.Combine(AppDataFolders.LocalApplicationData, "Lunima", "ComponentRegistry"));

    /// <summary>
    /// Tries to read a previously cached document. Returns null when the
    /// document is not cached or cannot be read.
    /// </summary>
    /// <param name="registryPath">Repo-relative path, e.g. <c>index.json</c>.</param>
    public string? TryRead(string registryPath)
    {
        var filePath = ToFilePath(registryPath);
        if (filePath is null || !File.Exists(filePath))
            return null;
        try
        {
            return File.ReadAllText(filePath);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Stores a downloaded document. Write failures are swallowed because a
    /// broken cache must never break a successful download.
    /// </summary>
    /// <param name="registryPath">Repo-relative path, e.g. <c>index.json</c>.</param>
    /// <param name="content">Raw document content.</param>
    public void Write(string registryPath, string content)
    {
        var filePath = ToFilePath(registryPath);
        if (filePath is null)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, content);
        }
        catch (IOException)
        {
            // Caching is best-effort; the caller already has the content.
        }
    }

    /// <summary>
    /// Maps a repo-relative URL path to a file path inside the cache root,
    /// rejecting traversal outside the root (returns null in that case).
    /// </summary>
    private string? ToFilePath(string registryPath)
    {
        var segments = registryPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Contains(".."))
            return null;
        return Path.Combine(_rootDirectory, Path.Combine(segments));
    }
}
