using System.Diagnostics;

namespace CAP_Core.Export;

/// <summary>
/// Builds fully-configured <see cref="ProcessStartInfo"/> instances for launching
/// external executables (Python, Docker, etc.). Executable resolution is delegated to
/// an <see cref="ExecutablePathProber"/> so that platform-specific well-known paths and
/// an augmented PATH are applied consistently without duplicating discovery logic.
/// </summary>
public sealed class ProcessLaunchFactory
{
    private readonly ExecutablePathProber _prober;

    /// <summary>
    /// Initializes the factory with the given path prober.
    /// </summary>
    /// <param name="prober">Prober used for executable resolution and PATH augmentation.</param>
    public ProcessLaunchFactory(ExecutablePathProber prober)
    {
        _prober = prober ?? throw new ArgumentNullException(nameof(prober));
    }

    /// <summary>
    /// Creates a factory backed by a default <see cref="ExecutablePathProber"/>. Use when a
    /// caller (a test or other non-DI construction) has no factory to inject; production code
    /// resolves the shared singleton from the DI container instead.
    /// </summary>
    public static ProcessLaunchFactory CreateDefault() => new ProcessLaunchFactory(new ExecutablePathProber());

    /// <summary>The path prober backing this factory — exposes the platform's well-known
    /// interpreter / conda locations to install-path scanners.</summary>
    public ExecutablePathProber Prober => _prober;

    // ─── Named basename constants ─────────────────────────────────────────────

    private const string Python3Basename = "python3";
    private const string PythonBasename  = "python";
    private const string DockerBasename  = "docker";

    // ─── Public API ──────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves <paramref name="command"/> to a runnable absolute path using the
    /// following strategy (never throws):
    /// <list type="number">
    ///   <item>If <paramref name="command"/> is already rooted and the file exists,
    ///         return it as-is.</item>
    ///   <item>If the basename matches a well-known tool (python3/python → Python paths;
    ///         docker → Docker paths), return the first path where
    ///         <see cref="File.Exists"/> is true.</item>
    ///   <item>Otherwise return <paramref name="command"/> unchanged so that
    ///         <see cref="Process.Start"/> can fall back to the OS PATH lookup.</item>
    /// </list>
    /// </summary>
    /// <param name="command">Command name or absolute path to resolve.</param>
    /// <returns>Resolved path, or the original <paramref name="command"/> value.</returns>
    public string? ResolveExecutable(string command)
    {
        if (string.IsNullOrEmpty(command))
            return command;

        // An explicit path (rooted, or containing a directory separator) is honored verbatim:
        // a caller who named a specific file must never have a *different* interpreter
        // substituted for it (even if that file does not exist — let Process.Start fail).
        // Only a bare command name is resolved against the well-known location lists.
        if (Path.IsPathRooted(command) ||
            command.Contains(Path.DirectorySeparatorChar) ||
            command.Contains(Path.AltDirectorySeparatorChar))
            return command;

        if (string.Equals(command, Python3Basename, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, PythonBasename,  StringComparison.OrdinalIgnoreCase))
        {
            var found = _prober.FirstExisting(_prober.WellKnownPythonPaths());
            if (found != null) return found;

            // On Windows the conventional interpreter command is `python`: installers register
            // python.exe on PATH, whereas a bare `python3` is usually the Microsoft Store
            // execution-alias stub (or absent). Returning `python` restores the pre-macOS-port
            // Windows behavior. On Linux the original command falls through to PATH below;
            // on macOS the well-known probing above already applied.
            if (OperatingSystem.IsWindows())
                return PythonBasename;
        }

        if (string.Equals(command, DockerBasename, StringComparison.OrdinalIgnoreCase))
        {
            var found = _prober.FirstExisting(_prober.WellKnownDockerPaths());
            if (found != null) return found;
        }

        // Fall back to PATH resolution at Process.Start time.
        return command;
    }

    /// <summary>
    /// Builds a fully-configured <see cref="ProcessStartInfo"/> ready for
    /// <see cref="Process.Start"/>. The caller is responsible for setting
    /// <c>Redirect*</c> properties and starting the process.
    /// <para>
    /// Configuration applied:
    /// <list type="bullet">
    ///   <item><c>FileName</c> is resolved via <see cref="ResolveExecutable"/>.</item>
    ///   <item><c>ArgumentList</c> is populated from <paramref name="arguments"/>
    ///         (the raw <c>Arguments</c> string is never set).</item>
    ///   <item><c>WorkingDirectory</c> is set to <paramref name="workingDirectory"/>
    ///         when provided, otherwise <see cref="AppContext.BaseDirectory"/>.</item>
    ///   <item><c>EnvironmentVariables["PATH"]</c> is set to
    ///         <see cref="ExecutablePathProber.AugmentedPath"/> of the current PATH.</item>
    ///   <item>Each entry in <paramref name="extraEnv"/> is merged into
    ///         <c>EnvironmentVariables</c> (e.g. <c>PYTHONPATH</c>).</item>
    ///   <item><c>UseShellExecute = false</c>, <c>CreateNoWindow = true</c>.</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="command">Executable name or path (will be resolved).</param>
    /// <param name="arguments">Ordered list of arguments; never concatenated by hand.</param>
    /// <param name="workingDirectory">Working directory, or <c>null</c> for
    ///     <see cref="AppContext.BaseDirectory"/>.</param>
    /// <param name="extraEnv">Additional environment variables to merge (may be null).</param>
    /// <param name="startInfo">The populated <see cref="ProcessStartInfo"/> on success;
    ///     a default instance on failure.</param>
    /// <param name="error">Human-readable error description on failure; otherwise null.</param>
    /// <returns><c>true</c> when <paramref name="startInfo"/> is usable;
    ///     <c>false</c> only for a genuinely unusable input (null/empty command).</returns>
    public bool TryBuild(
        string command,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string>? extraEnv,
        out ProcessStartInfo startInfo,
        out string? error)
    {
        startInfo = new ProcessStartInfo();
        error     = null;

        if (string.IsNullOrWhiteSpace(command))
        {
            error = "command must not be null or empty.";
            return false;
        }

        var resolved = ResolveExecutable(command) ?? command;

        startInfo = new ProcessStartInfo
        {
            FileName         = resolved,
            WorkingDirectory = workingDirectory ?? AppContext.BaseDirectory,
            UseShellExecute  = false,
            CreateNoWindow   = true,
        };

        foreach (var arg in arguments ?? Array.Empty<string>())
            startInfo.ArgumentList.Add(arg);

        var currentPath = Environment.GetEnvironmentVariable("PATH");
        startInfo.EnvironmentVariables["PATH"] = _prober.AugmentedPath(currentPath);

        if (extraEnv != null)
            foreach (var (key, value) in extraEnv)
                startInfo.EnvironmentVariables[key] = value;

        return true;
    }
}
