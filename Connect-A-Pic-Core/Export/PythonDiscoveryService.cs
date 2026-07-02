using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CAP_Core.Export;

/// <summary>
/// Discovers Python installations with Nazca in common locations.
/// Supports system Python, virtual environments, and conda environments.
/// </summary>
public class PythonDiscoveryService
{
    private readonly ProcessLaunchFactory _launchFactory;

    /// <summary>
    /// Initializes a new instance of <see cref="PythonDiscoveryService"/>.
    /// </summary>
    /// <param name="launchFactory">Factory used to build process start infos.</param>
    public PythonDiscoveryService(ProcessLaunchFactory? launchFactory = null)
    {
        _launchFactory = launchFactory ?? ProcessLaunchFactory.CreateDefault();
    }

    /// <summary>
    /// Information about a discovered Python installation.
    /// </summary>
    public class PythonInstallation
    {
        /// <summary>
        /// Path to the Python executable.
        /// </summary>
        public string Path { get; init; } = string.Empty;

        /// <summary>
        /// Source/origin of this installation (e.g., "System", "venv: nazca", "Active venv").
        /// </summary>
        public string Source { get; init; } = string.Empty;

        /// <summary>
        /// Python version string (e.g., "3.12.3").
        /// </summary>
        public string? PythonVersion { get; init; }

        /// <summary>
        /// Nazca version string (e.g., "0.6.1"), null if Nazca not installed.
        /// </summary>
        public string? NazcaVersion { get; init; }

        /// <summary>
        /// True if this Python has Nazca installed.
        /// </summary>
        public bool HasNazca => !string.IsNullOrEmpty(NazcaVersion);

        /// <summary>
        /// Display text for UI (e.g., "System Python 3.12 (Nazca 0.6.1)").
        /// </summary>
        public string DisplayText
        {
            get
            {
                var text = $"{Source}";
                if (PythonVersion != null)
                    text += $" Python {PythonVersion}";
                if (NazcaVersion != null)
                    text += $" (Nazca {NazcaVersion})";
                return text;
            }
        }
    }

    /// <summary>
    /// Discovers Python installations with Nazca in common locations.
    /// Searches system commands, active venv, and common venv directories.
    /// </summary>
    /// <returns>List of Python installations that have Nazca installed.</returns>
    public async Task<List<PythonInstallation>> DiscoverPythonWithNazcaAsync()
    {
        var found = new List<PythonInstallation>();
        var checkedPaths = new HashSet<string>();

        // 1. Check active virtual environment
        var venvPython = GetActiveVenvPython();
        if (venvPython != null && checkedPaths.Add(venvPython))
        {
            var installation = await CheckPythonInstallation(venvPython, "Active venv");
            if (installation?.HasNazca == true)
                found.Add(installation);
        }

        // 1b. Windows: full paths of installed interpreters (py-launcher / installer).
        // These resolve to real python.exe files that run reliably from a GUI process,
        // unlike the bare "py"/"python" PATH entries which are often Microsoft Store
        // execution-alias stubs that fail when launched with redirected I/O.
        foreach (var installPath in GetWindowsPythonInstallPaths())
        {
            if (checkedPaths.Add(installPath))
            {
                var installation = await CheckPythonInstallation(installPath, "Installed");
                if (installation?.HasNazca == true)
                    found.Add(installation);
            }
        }

        // 1c. macOS: full paths of installed interpreters (Homebrew, framework, pyenv).
        foreach (var installPath in GetMacOsPythonInstallPaths())
        {
            if (checkedPaths.Add(installPath))
            {
                var installation = await CheckPythonInstallation(installPath, "Installed");
                if (installation?.HasNazca == true)
                    found.Add(installation);
            }
        }

        // 2. Check standard system commands
        var systemCommands = GetSystemPythonCommands();
        foreach (var cmd in systemCommands)
        {
            var resolvedPath = ResolvePythonPath(cmd);
            if (resolvedPath != null && checkedPaths.Add(resolvedPath))
            {
                var installation = await CheckPythonInstallation(resolvedPath, "System");
                if (installation?.HasNazca == true)
                    found.Add(installation);
            }
        }

        // 3. Search common venv directories
        var venvDirs = GetCommonVenvDirectories();
        foreach (var venvPath in venvDirs)
        {
            if (checkedPaths.Add(venvPath))
            {
                var installation = await CheckPythonInstallation(venvPath, GetVenvSource(venvPath));
                if (installation?.HasNazca == true)
                    found.Add(installation);
            }
        }

        return found;
    }

    /// <summary>
    /// Returns the path of the first discovered Python that can import nazca, checking
    /// sources in priority order (active venv → installed interpreters → "py" launcher →
    /// common venvs) and stopping at the first match. Cheaper than
    /// <see cref="DiscoverPythonWithNazcaAsync"/> for the common "just give me one that
    /// works" case (e.g. resolving the preview interpreter at startup), since it does not
    /// probe every candidate. Returns <c>null</c> if none is found.
    /// </summary>
    public async Task<string?> FindFirstNazcaPythonPathAsync()
    {
        var venv = GetActiveVenvPython();
        if (venv != null && await FirstWithNazca(venv, "Active venv") is { } v)
            return v;

        foreach (var installPath in GetWindowsPythonInstallPaths())
            if (await FirstWithNazca(installPath, "Installed") is { } p)
                return p;

        foreach (var installPath in GetMacOsPythonInstallPaths())
            if (await FirstWithNazca(installPath, "Installed") is { } p)
                return p;

        foreach (var cmd in GetSystemPythonCommands())
            if (await FirstWithNazca(cmd, "System") is { } p)
                return p;

        foreach (var venvPath in GetCommonVenvDirectories())
            if (await FirstWithNazca(venvPath, GetVenvSource(venvPath)) is { } p)
                return p;

        return null;
    }

    /// <summary>Returns the path if the candidate has nazca, otherwise null.</summary>
    private async Task<string?> FirstWithNazca(string path, string source)
    {
        var install = await CheckPythonInstallation(path, source);
        return install?.HasNazca == true ? install.Path : null;
    }

    /// <summary>
    /// Checks if a specific Python path has Nazca installed and retrieves version info.
    /// </summary>
    /// <param name="pythonPath">Path to Python executable or command name.</param>
    /// <returns>Installation info with versions, or null if Python not accessible.</returns>
    public async Task<PythonInstallation?> CheckPythonInstallation(string pythonPath, string source)
    {
        try
        {
            var pythonVersion = await GetPythonVersion(pythonPath);
            if (pythonVersion == null)
                return null;

            var nazcaVersion = await GetNazcaVersion(pythonPath);

            return new PythonInstallation
            {
                Path = pythonPath,
                Source = source,
                PythonVersion = pythonVersion,
                NazcaVersion = nazcaVersion
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the Python executable path from the active virtual environment.
    /// Checks VIRTUAL_ENV environment variable.
    /// </summary>
    private static string? GetActiveVenvPython()
    {
        var venvPath = Environment.GetEnvironmentVariable("VIRTUAL_ENV");
        if (string.IsNullOrEmpty(venvPath))
            return null;

        var pythonPath = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? System.IO.Path.Combine(venvPath, "Scripts", "python.exe")
            : System.IO.Path.Combine(venvPath, "bin", "python");

        return File.Exists(pythonPath) ? pythonPath : null;
    }

    /// <summary>
    /// Gets standard Python command names for the current platform, probing absolute
    /// well-known paths via the launch factory before falling back to PATH names.
    /// On Windows only the "py" launcher is returned (Microsoft Store stubs are avoided).
    /// </summary>
    private IReadOnlyList<string> GetSystemPythonCommands()
    {
        // On Windows we deliberately probe ONLY "py" (the official launcher), never
        // bare "python"/"python3": those usually resolve to the Microsoft Store
        // execution-alias stub, and starting that stub from a GUI process can block
        // indefinitely (it triggers Store/App-Installer activation that never returns).
        // Real interpreters are found via the filesystem scan in
        // GetWindowsPythonInstallPaths instead.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new[] { "py" };

        // On macOS and Linux, probe absolute well-known paths first so the factory's
        // resolver is consulted before falling back to unqualified PATH names.
        var resolved3 = _launchFactory.ResolveExecutable("python3");
        var resolved  = _launchFactory.ResolveExecutable("python");

        var commands = new List<string>();
        if (resolved3 != null) commands.Add(resolved3);
        if (resolved  != null && resolved != resolved3) commands.Add(resolved);
        return commands;
    }

    /// <summary>
    /// Searches common virtual environment directories for Python installations.
    /// </summary>
    private static List<string> GetCommonVenvDirectories()
    {
        var pythonPaths = new List<string>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // ~/.venvs/*/bin/python or ~/.venvs/*/Scripts/python.exe
        var venvsDir = System.IO.Path.Combine(home, ".venvs");
        if (Directory.Exists(venvsDir))
        {
            pythonPaths.AddRange(FindPythonInVenvs(venvsDir));
        }

        // <AppBase>/venv/bin/python — use AppContext.BaseDirectory, not
        // Directory.GetCurrentDirectory(): on macOS Finder launches the app with cwd='/'
        // so a relative probe would always miss.
        var localVenv = System.IO.Path.Combine(AppContext.BaseDirectory, "venv");
        if (Directory.Exists(localVenv))
        {
            var pythonPath = GetPythonPathInVenv(localVenv);
            if (pythonPath != null)
                pythonPaths.Add(pythonPath);
        }

        return pythonPaths;
    }

    /// <summary>
    /// Finds full paths of installed Python interpreters on Windows by scanning the
    /// standard install roots used by the py-launcher / python.org installers:
    /// <c>%LOCALAPPDATA%\Python\*</c> (py-manager pythoncore builds),
    /// <c>%LOCALAPPDATA%\Programs\Python\*</c> (per-user installer) and
    /// <c>%ProgramFiles%\Python*</c> (machine-wide installer). Returns an empty list
    /// on non-Windows platforms. Each path is a real <c>python.exe</c> so it can be
    /// launched directly without going through a PATH execution-alias stub.
    /// </summary>
    private static List<string> GetWindowsPythonInstallPaths()
        => PythonInstallPathScanner.WindowsInstallPaths();

    /// <summary>
    /// Finds full paths of installed Python interpreters on macOS by consulting the
    /// launch factory's well-known path list (Homebrew, /usr/local, Python.framework,
    /// pyenv shims). Returns an empty list on non-macOS platforms. Each returned path
    /// passes <see cref="File.Exists"/> so callers can launch without PATH fallback.
    /// </summary>
    private List<string> GetMacOsPythonInstallPaths()
        => PythonInstallPathScanner.MacOsInstallPaths(_launchFactory);

    /// <summary>
    /// Finds Python executables in subdirectories of a venv parent directory.
    /// </summary>
    private static List<string> FindPythonInVenvs(string venvsParentDir)
    {
        var pythonPaths = new List<string>();

        try
        {
            foreach (var dir in Directory.GetDirectories(venvsParentDir))
            {
                var pythonPath = GetPythonPathInVenv(dir);
                if (pythonPath != null)
                    pythonPaths.Add(pythonPath);
            }
        }
        catch
        {
            // Ignore access errors
        }

        return pythonPaths;
    }

    /// <summary>
    /// Gets the Python executable path within a venv directory.
    /// </summary>
    private static string? GetPythonPathInVenv(string venvDir)
    {
        var pythonPath = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? System.IO.Path.Combine(venvDir, "Scripts", "python.exe")
            : System.IO.Path.Combine(venvDir, "bin", "python");

        return File.Exists(pythonPath) ? pythonPath : null;
    }

    /// <summary>
    /// Extracts a user-friendly source name from a venv path.
    /// </summary>
    private static string GetVenvSource(string venvPath)
    {
        var dirName = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(venvPath));
        return string.IsNullOrEmpty(dirName) ? "venv" : $"venv: {dirName}";
    }

    /// <summary>
    /// Resolves a Python command to its full path for deduplication, using the launch
    /// factory's executable resolver to probe absolute well-known paths before PATH.
    /// </summary>
    private string? ResolvePythonPath(string command)
    {
        var resolved = _launchFactory.ResolveExecutable(command);
        if (resolved == null) return null;

        // If the factory returned an absolute path that exists, use it directly.
        if (System.IO.Path.IsPathRooted(resolved) && File.Exists(resolved))
            return resolved;

        // The factory fell back to the bare name (PATH resolution at Start time);
        // return it as-is so the caller can still deduplicate by command name.
        return resolved;
    }

    /// <summary>
    /// Gets the Python version string.
    /// </summary>
    private async Task<string?> GetPythonVersion(string pythonPath)
    {
        try
        {
            var (exitCode, output, _) = await RunCommandAsync(pythonPath, new[] { "--version" });

            if (exitCode == 0 && !string.IsNullOrWhiteSpace(output))
            {
                var parts = output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return parts.Length >= 2 ? parts[1] : output.Trim();
            }
        }
        catch
        {
            // Python not accessible
        }

        return null;
    }

    /// <summary>
    /// Gets the Nazca version string, or null if not installed.
    /// Routes the spawn through <see cref="ProcessLaunchFactory.TryBuild"/> with
    /// <c>ArgumentList</c> so the interpreter path is resolved and arguments are never
    /// hand-quoted.
    /// </summary>
    private async Task<string?> GetNazcaVersion(string pythonPath)
    {
        try
        {
            var checkScript = "import nazca; print(nazca.__version__)";
            IReadOnlyList<string> args = new[] { "-c", checkScript };

            if (!_launchFactory.TryBuild(pythonPath, args, null, null, out var psi, out var launchError))
                return null;

            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError  = true;

            using var process = Process.Start(psi);
            if (process == null)
                return null;

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask  = process.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(NazcaImportTimeoutMs);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return null;
            }

            var output = await outputTask;
            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                return output.Trim();
        }
        catch
        {
            // Nazca not installed
        }

        return null;
    }

    /// <summary>
    /// Runs a command with the given argument list, capturing output and killing it if it
    /// does not exit within <paramref name="timeoutMs"/>. The timeout is essential on
    /// Windows: a bare "python"/"python3" command can be a Microsoft Store
    /// execution-alias stub that hangs instead of exiting — without a kill that would
    /// block discovery forever. A timed-out command is reported as a failure (exit -1).
    /// </summary>
    private async Task<(int exitCode, string output, string error)> RunCommandAsync(
        string fileName, IReadOnlyList<string> arguments, int timeoutMs = DefaultCommandTimeoutMs)
    {
        if (!_launchFactory.TryBuild(fileName, arguments, null, null, out var psi, out _))
            return (-1, string.Empty, "Failed to build process start info.");

        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError  = true;

        using var process = Process.Start(psi);
        if (process == null)
            return (-1, string.Empty, "Process.Start returned null.");

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask  = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return (-1, string.Empty, "Command timed out.");
        }

        var output = await outputTask;
        var error  = await errorTask;

        return (process.ExitCode, output, error);
    }

    /// <summary>Default timeout for a probe command (version check). Real interpreters
    /// answer in well under a second; a longer wait means a hung Store-alias stub.</summary>
    private const int DefaultCommandTimeoutMs = 5000;

    /// <summary>Timeout for the nazca import check — generous because importing nazca
    /// (and its heavy deps) on a cold interpreter can legitimately take several seconds.</summary>
    private const int NazcaImportTimeoutMs = 20000;
}
