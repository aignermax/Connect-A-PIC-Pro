namespace CAP_Core.Export;

/// <summary>
/// Probes the filesystem for well-known locations of common executables (Python, Docker)
/// and provides an augmented PATH that includes platform-specific binary directories.
/// All OS-specific path tables are guarded by <see cref="OperatingSystem"/> checks so they
/// remain empty/no-op on unsupported platforms.
/// </summary>
public sealed class ExecutablePathProber
{
    // ─── Named path constants ────────────────────────────────────────────────

    private static readonly string HomeDir =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } h
            ? h
            : Environment.GetEnvironmentVariable("HOME") ?? string.Empty;

    // macOS Python locations
    private const string MacBrewBin          = "/opt/homebrew/bin";
    private const string MacLocalBin         = "/usr/local/bin";
    private const string MacPython3Brew      = MacBrewBin  + "/python3";
    private const string MacPython3Local     = MacLocalBin + "/python3";
    private const string PythonFrameworkDir  = "/Library/Frameworks/Python.framework/Versions/Current/bin/python3";
    private static readonly string PyenvShimDir = Path.Combine(HomeDir, ".pyenv", "shims", "python3");

    // macOS Docker locations
    private const string MacBrewDocker   = MacBrewBin  + "/docker";
    private const string MacLocalDocker  = MacLocalBin + "/docker";
    private const string DockerAppBinDir = "/Applications/Docker.app/Contents/Resources/bin/docker";

    // macOS augmented PATH directories (prepended when not already present)
    private static readonly string[] MacOsBinDirs = { MacBrewBin, MacLocalBin };

    // ─── Public API ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns well-known filesystem paths for Python 3 to probe.
    /// <list type="bullet">
    ///   <item>macOS: Homebrew, /usr/local, Python.framework, pyenv shims, conda bases</item>
    ///   <item>Linux/Windows: empty — see remarks</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// Probing is macOS-only by design. A Finder/Dock launch on macOS does not inherit a
    /// shell-initialised PATH, so the interpreter must be located explicitly. On Linux and
    /// Windows a normally-launched process keeps a correct PATH, so returning nothing lets
    /// PATH resolution stand — overriding it would wrongly pin a system interpreter that may
    /// lack the user's packages (e.g. nazca). This preserves the pre-existing Linux/Windows
    /// behavior; the macOS-specific resolution is purely additive.
    /// </remarks>
    public IReadOnlyList<string> WellKnownPythonPaths()
    {
        if (OperatingSystem.IsMacOS())
        {
            var paths = new List<string> { MacPython3Brew, MacPython3Local, PythonFrameworkDir, PyenvShimDir };
            paths.AddRange(CondaBasePythonPaths());
            return paths;
        }

        return Array.Empty<string>();
    }

    /// <summary>
    /// Candidate conda / anaconda / miniforge install roots for the current platform
    /// (empty on unsupported platforms). The base interpreter lives at
    /// <c>&lt;root&gt;/bin/python3</c>; named environments under <c>&lt;root&gt;/envs/</c>.
    /// A Finder/Dock launch does not inherit conda's shell-activated PATH, so these
    /// well-known roots are probed directly — conda is the dominant macOS Python setup.
    /// </summary>
    public IReadOnlyList<string> CondaRootDirectories()
    {
        if (OperatingSystem.IsMacOS())
            return new[]
            {
                "/opt/homebrew/Caskroom/miniconda/base",
                "/opt/homebrew/Caskroom/miniforge/base",
                Path.Combine(HomeDir, "miniconda3"),
                Path.Combine(HomeDir, "anaconda3"),
                Path.Combine(HomeDir, "miniforge3"),
                Path.Combine(HomeDir, "mambaforge"),
                "/opt/miniconda3",
                "/opt/anaconda3",
            };

        if (OperatingSystem.IsLinux())
            return new[]
            {
                Path.Combine(HomeDir, "miniconda3"),
                Path.Combine(HomeDir, "anaconda3"),
                Path.Combine(HomeDir, "miniforge3"),
                Path.Combine(HomeDir, "mambaforge"),
                "/opt/miniconda3",
                "/opt/anaconda3",
            };

        return Array.Empty<string>();
    }

    /// <summary>Base-environment interpreter paths (<c>&lt;root&gt;/bin/python3</c>) for each candidate conda root.</summary>
    private IEnumerable<string> CondaBasePythonPaths()
    {
        foreach (var root in CondaRootDirectories())
            yield return Path.Combine(root, "bin", "python3");
    }

    /// <summary>
    /// Returns well-known filesystem paths for the Docker CLI to probe (macOS only:
    /// Homebrew, /usr/local, Docker Desktop bundle). Empty on Linux/Windows so PATH
    /// resolution stands — see <see cref="WellKnownPythonPaths"/> for the rationale.
    /// </summary>
    public IReadOnlyList<string> WellKnownDockerPaths()
    {
        if (OperatingSystem.IsMacOS())
            return new[] { MacBrewDocker, MacLocalDocker, DockerAppBinDir };

        return Array.Empty<string>();
    }

    /// <summary>
    /// Returns a PATH string suitable for use as the <c>PATH</c> environment variable
    /// when launching subprocesses. On macOS, <see cref="MacOsBinDirs"/> are prepended
    /// (only if not already present) so that Homebrew and /usr/local tools are found even
    /// when the host process was launched without a shell-initialised PATH. On all other
    /// platforms the <paramref name="existingPath"/> is returned unchanged (or an empty
    /// string when <paramref name="existingPath"/> is null).
    /// </summary>
    /// <param name="existingPath">Current value of the PATH variable; may be null.</param>
    public string AugmentedPath(string? existingPath)
    {
        if (!OperatingSystem.IsMacOS())
            return existingPath ?? string.Empty;

        var separator = Path.PathSeparator.ToString();
        var parts = (existingPath ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        // Prepend missing macOS dirs in reverse so the order is preserved front-to-back.
        for (int i = MacOsBinDirs.Length - 1; i >= 0; i--)
        {
            var dir = MacOsBinDirs[i];
            if (!parts.Contains(dir, StringComparer.Ordinal))
                parts.Insert(0, dir);
        }

        return string.Join(separator, parts);
    }

    /// <summary>
    /// Returns the first path in <paramref name="candidates"/> for which
    /// <see cref="File.Exists"/> is true, or <c>null</c> if none exists.
    /// </summary>
    /// <param name="candidates">Ordered sequence of absolute paths to probe.</param>
    public string? FirstExisting(IEnumerable<string> candidates)
    {
        foreach (var path in candidates)
            if (File.Exists(path))
                return path;
        return null;
    }
}
