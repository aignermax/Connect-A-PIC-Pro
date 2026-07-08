namespace CAP_Core.Helpers;

/// <summary>
/// Resolves the per-user application-data root in a snap-safe way. On Linux,
/// processes launched from a snap-confined terminal (e.g. the snap build of
/// VS Code) inherit an <c>XDG_DATA_HOME</c> pointing into that snap's private
/// world (<c>$HOME/snap/&lt;app&gt;/&lt;revision&gt;/.local/share</c>). Lunima itself is
/// not shipped as a snap, so honouring that value would split preferences and
/// managed Python environments into a second, launcher-dependent data
/// directory. This helper detects the foreign redirect and falls back to the
/// real <c>$HOME/.local/share</c>.
/// </summary>
public static class AppDataFolders
{
    private static string? _cached;

    /// <summary>
    /// Snap-safe equivalent of
    /// <c>Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)</c>.
    /// </summary>
    public static string LocalApplicationData => _cached ??= Resolve();

    private static string Resolve()
    {
        var osProvided = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!OperatingSystem.IsLinux())
            return osProvided;

        return ResolveLinux(
            osProvided,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    /// <summary>
    /// Pure resolution core, separated for unit tests: returns the real
    /// <c>~/.local/share</c> when <paramref name="osProvidedPath"/> points inside
    /// another snap's redirected data directory; otherwise the OS-provided
    /// value verbatim.
    /// </summary>
    internal static string ResolveLinux(string osProvidedPath, string homeDirectory)
    {
        if (string.IsNullOrEmpty(osProvidedPath) || string.IsNullOrEmpty(homeDirectory))
            return osProvidedPath;

        // This function only ever sees Linux paths, so the separator is a
        // literal '/' — Path.Combine would inject '\' when the test suite
        // runs on a Windows dev machine and break the comparison.
        var home = homeDirectory.TrimEnd('/');
        return osProvidedPath.StartsWith(home + "/snap/", StringComparison.Ordinal)
            ? home + "/.local/share"
            : osProvidedPath;
    }
}
