using System.Diagnostics;

namespace CAP.Avalonia.Services.Update;

/// <summary>
/// Describes where the running application is installed, so an updater can replace it and relaunch.
/// </summary>
public sealed class InstallLocation
{
    /// <summary>The directory or bundle to replace — the <c>.app</c> on macOS, the install directory on Windows/Linux.</summary>
    public string Root { get; }

    /// <summary>Full path to the executable, used to relaunch on Windows/Linux and to locate the bundle on macOS.</summary>
    public string ExecutablePath { get; }

    /// <summary>PID of the currently running process; the updater waits for it to exit before swapping.</summary>
    public int ProcessId { get; }

    /// <summary>Initializes a new <see cref="InstallLocation"/>.</summary>
    public InstallLocation(string root, string executablePath, int processId)
    {
        Root = root;
        ExecutablePath = executablePath;
        ProcessId = processId;
    }

    /// <summary>
    /// Resolves the install location of the running process, or null if it can't be determined
    /// (e.g. launched via <c>dotnet run</c> from a build output rather than an installed app).
    /// On macOS the root is the enclosing <c>*.app</c> bundle; on Windows/Linux it is the
    /// executable's directory.
    /// </summary>
    public static InstallLocation? Resolve()
    {
        string? exe;
        try
        {
            exe = Process.GetCurrentProcess().MainModule?.FileName;
        }
        catch
        {
            return null;
        }

        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            return null;

        var pid = Environment.ProcessId;

        if (OperatingSystem.IsMacOS())
        {
            var bundle = FindEnclosingAppBundle(exe);
            return bundle is null ? null : new InstallLocation(bundle, exe, pid);
        }

        var dir = Path.GetDirectoryName(exe);
        return string.IsNullOrEmpty(dir) ? null : new InstallLocation(dir, exe, pid);
    }

    /// <summary>
    /// True when <paramref name="directory"/> is a shared user directory that the app does not
    /// own exclusively (the user's home, its standard subfolders like Downloads/Desktop/Documents,
    /// the temp directory, or a filesystem root). The in-place updater must never treat such a
    /// directory as a replaceable install root — the Linux portable build is a loose binary whose
    /// "install dir" is wherever the user dropped it, and replacing/removing the whole directory
    /// would destroy unrelated files (issue #616).
    /// </summary>
    internal static bool IsSharedUserDirectory(string directory)
    {
        if (string.IsNullOrEmpty(directory))
            return false;

        var target = Canonical(directory);

        // Filesystem root (e.g. "/", "C:\").
        var root = Path.GetPathRoot(directory);
        if (!string.IsNullOrEmpty(root) && Canonical(root) == target)
            return true;

        if (target == Canonical(Path.GetTempPath()))
            return true;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home) && Canonical(home) == target)
            return true;

        foreach (var s in SharedSubdirectories(home))
            if (!string.IsNullOrEmpty(s) && Canonical(s) == target)
                return true;

        return false;
    }

    /// <summary>
    /// Standard per-user folders that are never an app's exclusive install root. Resolves through
    /// the OS SpecialFolder enum (localized/redirected paths included) and, on Linux, honours the
    /// XDG user-dirs config so e.g. a localized "Téléchargements" download folder is recognised.
    /// </summary>
    private static IEnumerable<string> SharedSubdirectories(string? home)
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

        // Downloads has no SpecialFolder enum. English default under home…
        if (!string.IsNullOrEmpty(home))
            yield return Path.Combine(home, "Downloads");

        // …plus the XDG-configured (possibly localized) download/desktop/documents dirs on Linux.
        if (OperatingSystem.IsLinux())
        {
            foreach (var d in XdgUserDirs())
                yield return d;
        }
    }

    /// <summary>
    /// Reads the XDG user-dirs (env var first, then <c>~/.config/user-dirs.dirs</c>) and returns the
    /// resolved Download/Desktop/Documents paths. Best-effort: returns nothing on any parse error.
    /// </summary>
    private static IEnumerable<string> XdgUserDirs()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var results = new List<string>();

        void AddFromEnv(string var)
        {
            var v = Environment.GetEnvironmentVariable(var);
            if (!string.IsNullOrEmpty(v)) results.Add(v);
        }
        AddFromEnv("XDG_DOWNLOAD_DIR");
        AddFromEnv("XDG_DESKTOP_DIR");
        AddFromEnv("XDG_DOCUMENTS_DIR");

        try
        {
            var config = Path.Combine(home, ".config", "user-dirs.dirs");
            if (File.Exists(config))
            {
                foreach (var raw in File.ReadAllLines(config))
                {
                    var line = raw.Trim();
                    // Format: XDG_DOWNLOAD_DIR="$HOME/Downloads"
                    if (!line.StartsWith("XDG_", StringComparison.Ordinal)) continue;
                    var eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    var value = line[(eq + 1)..].Trim().Trim('"');
                    value = value.Replace("$HOME", home).Replace("${HOME}", home);
                    if (!string.IsNullOrEmpty(value)) results.Add(value);
                }
            }
        }
        catch { /* best-effort — the file-by-file updater is the primary safeguard */ }

        return results;
    }

    /// <summary>
    /// Canonical path for comparison: absolute, trailing separators trimmed, and symlinks resolved
    /// to their final target when the path exists (so a symlinked shared dir still matches).
    /// </summary>
    private static string Canonical(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var resolved = Directory.ResolveLinkTarget(full, returnFinalTarget: true);
            return Trim(resolved?.FullName ?? full);
        }
        catch
        {
            return Trim(path);
        }
    }

    private static string Trim(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// Walks up from <paramref name="executablePath"/> to the nearest enclosing <c>*.app</c>
    /// bundle directory, or null if the executable is not inside a bundle.
    /// </summary>
    internal static string? FindEnclosingAppBundle(string executablePath)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(executablePath) ?? executablePath);
        while (dir is not null)
        {
            if (dir.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                return dir.FullName;
            dir = dir.Parent;
        }

        return null;
    }
}
