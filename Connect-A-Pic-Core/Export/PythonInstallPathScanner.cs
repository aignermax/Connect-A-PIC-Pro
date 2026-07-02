namespace CAP_Core.Export;

/// <summary>
/// Scans the filesystem for installed Python interpreters using each platform's standard
/// install locations. Kept separate from <see cref="PythonDiscoveryService"/> so the
/// discovery orchestration stays focused and within the per-file size budget.
/// </summary>
internal static class PythonInstallPathScanner
{
    /// <summary>
    /// Full paths of installed Python interpreters on Windows by scanning the standard
    /// install roots used by the py-launcher / python.org installers:
    /// <c>%LOCALAPPDATA%\Python\*</c>, <c>%LOCALAPPDATA%\Programs\Python\*</c> and
    /// <c>%ProgramFiles%\Python*</c>. Returns an empty list on non-Windows platforms.
    /// Each path is a real <c>python.exe</c> so it can be launched directly without going
    /// through a PATH execution-alias stub.
    /// </summary>
    public static List<string> WindowsInstallPaths()
    {
        var paths = new List<string>();
        if (!OperatingSystem.IsWindows())
            return paths;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        // (root, pattern) — pattern restricts the ProgramFiles scan to "Python*"
        // so we don't enumerate every unrelated Program Files folder.
        var roots = new[]
        {
            (System.IO.Path.Combine(localAppData, "Python"), "*"),
            (System.IO.Path.Combine(localAppData, "Programs", "Python"), "*"),
            (programFiles, "Python*"),
        };

        foreach (var (root, pattern) in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var dir in Directory.GetDirectories(root, pattern))
                {
                    var exe = System.IO.Path.Combine(dir, "python.exe");
                    if (File.Exists(exe))
                        paths.Add(exe);
                }
            }
            catch
            {
                // Ignore access errors — a single unreadable root must not abort discovery.
            }
        }

        return paths;
    }

    /// <summary>
    /// Full paths of installed Python interpreters on macOS, taken from the launch
    /// factory's well-known list (Homebrew, /usr/local, Python.framework, pyenv shims).
    /// Returns an empty list on non-macOS platforms. Every returned path passes
    /// <see cref="File.Exists"/> so callers can launch without PATH fallback.
    /// </summary>
    public static List<string> MacOsInstallPaths(ProcessLaunchFactory launchFactory)
    {
        var paths = new List<string>();
        if (!OperatingSystem.IsMacOS())
            return paths;

        var prober = launchFactory.Prober;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Every existing well-known interpreter — Homebrew, /usr/local, Python.framework,
        // pyenv, and conda/anaconda/miniforge BASE environments. Returning all of them (not
        // just the first) lets the discovery layer test each one for nazca.
        foreach (var p in prober.WellKnownPythonPaths())
            if (File.Exists(p) && seen.Add(p))
                paths.Add(p);

        // Named conda environments: <root>/envs/<name>/bin/python3.
        foreach (var p in CondaEnvPythonPaths(prober.CondaRootDirectories()))
            if (seen.Add(p))
                paths.Add(p);

        return paths;
    }

    /// <summary>
    /// Python interpreters inside named conda environments — <c>&lt;root&gt;/envs/*/bin/python3</c>
    /// — for each given conda install root that exists. A pure filesystem scan, so it is
    /// testable against a temporary directory tree.
    /// </summary>
    public static List<string> CondaEnvPythonPaths(IEnumerable<string> condaRoots)
    {
        var result = new List<string>();
        foreach (var root in condaRoots)
        {
            var envsDir = System.IO.Path.Combine(root, "envs");
            if (!Directory.Exists(envsDir))
                continue;
            try
            {
                foreach (var env in Directory.GetDirectories(envsDir))
                {
                    var py = System.IO.Path.Combine(env, "bin", "python3");
                    if (File.Exists(py))
                        result.Add(py);
                }
            }
            catch
            {
                // Ignore an unreadable root — one bad directory must not abort discovery.
            }
        }
        return result;
    }
}
