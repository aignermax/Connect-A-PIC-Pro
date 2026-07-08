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

        var norm = Normalize(directory);

        // Filesystem root (e.g. "/", "C:\").
        var root = Path.GetPathRoot(directory);
        if (!string.IsNullOrEmpty(root) && Normalize(root) == norm)
            return true;

        if (norm == Normalize(Path.GetTempPath()))
            return true;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            if (norm == Normalize(home))
                return true;

            // Standard per-user folders. Downloads has no SpecialFolder enum, so it is matched
            // by name; the rest resolve through the OS so localized/XDG-redirected paths count too.
            var shared = new[]
            {
                Path.Combine(home, "Downloads"),
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            };
            foreach (var s in shared)
                if (!string.IsNullOrEmpty(s) && Normalize(s) == norm)
                    return true;
        }

        return false;
    }

    /// <summary>Case-preserving path normalization for comparison: trims trailing separators.</summary>
    private static string Normalize(string path) =>
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
