using CAP_Core.Helpers;

namespace CAP_Core.ComponentRegistry;

/// <summary>
/// File-based cache for raw registry JSON documents, keyed by their
/// repo-relative path. Lives under the Lunima app-data directory so the
/// registry stays browsable offline once it has been fetched.
/// </summary>
public sealed class RegistryCache
{
    private readonly string _rootDirectory;

    /// <summary>
    /// Initialises the cache.
    /// </summary>
    /// <param name="rootDirectory">Cache directory; null uses the default under the Lunima app-data folder.</param>
    public RegistryCache(string? rootDirectory = null)
    {
        _rootDirectory = rootDirectory ?? Path.Combine(
            AppDataFolders.LocalApplicationData, "Lunima", "registry-cache");
    }

    /// <summary>
    /// Reads the cached document for <paramref name="relativePath"/>,
    /// or null if it is not cached.
    /// </summary>
    public string? Read(string relativePath)
    {
        var file = ResolveSafe(relativePath);
        if (file == null || !File.Exists(file))
            return null;
        return File.ReadAllText(file);
    }

    /// <summary>Stores <paramref name="content"/> as the cached document for <paramref name="relativePath"/>.</summary>
    public void Write(string relativePath, string content)
    {
        var file = ResolveSafe(relativePath);
        if (file == null)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
    }

    /// <summary>
    /// Maps a repo-relative path to a file inside the cache root, rejecting
    /// paths that would escape it (rooted paths or ".." segments).
    /// </summary>
    private string? ResolveSafe(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            return null;
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s == ".."))
            return null;
        return Path.Combine(new[] { _rootDirectory }.Concat(segments).ToArray());
    }
}
