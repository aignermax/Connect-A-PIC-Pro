using System.Diagnostics;

namespace CAP_Core.Export;

/// <summary>
/// Core service for GDS export functionality.
/// Detects Python/Nazca availability and executes Python scripts to generate GDS files.
/// </summary>
public class GdsExportService
{
    private const string MinimumNazcaVersion = "0.5.0";
    private const string DefaultPythonCommand = "python3";

    private readonly ProcessLaunchFactory _launchFactory;
    private readonly PythonDiscoveryService _pythonDiscovery;
    private string? _customPythonPath;

    /// <summary>
    /// Initializes the service with a process launch factory and Python discovery service.
    /// </summary>
    /// <param name="launchFactory">Factory used to build process start info.</param>
    /// <param name="pythonDiscovery">Service used to locate a Python interpreter with Nazca.</param>
    public GdsExportService(ProcessLaunchFactory? launchFactory = null, PythonDiscoveryService? pythonDiscovery = null)
    {
        _launchFactory   = launchFactory   ?? ProcessLaunchFactory.CreateDefault();
        _pythonDiscovery = pythonDiscovery ?? new PythonDiscoveryService(_launchFactory);
    }

    /// <summary>
    /// Result of a GDS export operation.
    /// </summary>
    public class ExportResult
    {
        /// <summary>
        /// Path to the exported Python script.
        /// </summary>
        public string ScriptPath { get; init; } = string.Empty;

        /// <summary>
        /// Path to the generated GDS file (null if generation failed or was skipped).
        /// </summary>
        public string? GdsPath { get; init; }

        /// <summary>
        /// Status message describing the export outcome.
        /// </summary>
        public string Status { get; init; } = string.Empty;

        /// <summary>
        /// True if both script and GDS were successfully created.
        /// </summary>
        public bool Success { get; init; }

        /// <summary>
        /// Error message if export failed.
        /// </summary>
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Information about Python environment availability.
    /// </summary>
    public class PythonEnvironmentInfo
    {
        /// <summary>
        /// True if Python executable was found.
        /// </summary>
        public bool PythonAvailable { get; set; }

        /// <summary>
        /// Python version string (e.g., "3.10.5").
        /// </summary>
        public string? PythonVersion { get; set; }

        /// <summary>
        /// True if Nazca package is installed.
        /// </summary>
        public bool NazcaAvailable { get; set; }

        /// <summary>
        /// Nazca version string (e.g., "0.5.10").
        /// </summary>
        public string? NazcaVersion { get; set; }

        /// <summary>
        /// True if both Python and Nazca are available.
        /// </summary>
        public bool IsReady => PythonAvailable && NazcaAvailable;

        /// <summary>
        /// Descriptive status message for UI display.
        /// </summary>
        public string StatusMessage
        {
            get
            {
                if (!PythonAvailable)
                    return "Python not found";
                if (!NazcaAvailable)
                    return "Nazca not installed";
                return $"Python {PythonVersion}, Nazca {NazcaVersion}";
            }
        }
    }

    /// <summary>
    /// Sets a custom Python path to use instead of system default.
    /// </summary>
    /// <param name="pythonPath">Path to Python executable, or null to use system default.</param>
    public void SetCustomPythonPath(string? pythonPath)
    {
        _customPythonPath = pythonPath;
    }

    /// <summary>
    /// Gets the currently configured Python path (custom or default).
    /// </summary>
    public string GetCurrentPythonPath()
    {
        return _customPythonPath ?? _launchFactory.ResolveExecutable(DefaultPythonCommand) ?? DefaultPythonCommand;
    }

    /// <summary>
    /// Checks if Python and Nazca are available in the system.
    /// Uses custom Python path if configured, otherwise uses system default.
    /// </summary>
    /// <returns>Environment information including versions.</returns>
    public async Task<PythonEnvironmentInfo> CheckPythonEnvironmentAsync()
    {
        var result = new PythonEnvironmentInfo();

        var pythonVersion = await GetPythonVersionAsync();
        if (!string.IsNullOrEmpty(pythonVersion))
        {
            result.PythonAvailable = true;
            result.PythonVersion = pythonVersion;

            var nazcaVersion = await GetNazcaVersionAsync();
            if (!string.IsNullOrEmpty(nazcaVersion))
            {
                result.NazcaAvailable = true;
                result.NazcaVersion = nazcaVersion;
            }
        }

        return result;
    }

    /// <summary>
    /// Exports to GDS by executing a Python script.
    /// </summary>
    /// <param name="scriptPath">Path to the Python script to execute.</param>
    /// <param name="generateGds">If true, attempts to generate GDS from the script.</param>
    /// <returns>Export result with status information.</returns>
    public async Task<ExportResult> ExportToGdsAsync(string scriptPath, bool generateGds)
    {
        if (!File.Exists(scriptPath))
        {
            return new ExportResult
            {
                ScriptPath = scriptPath,
                Success = false,
                ErrorMessage = "Script file not found"
            };
        }

        if (!generateGds)
        {
            return new ExportResult
            {
                ScriptPath = scriptPath,
                Success = true,
                Status = "Script exported (GDS generation skipped)"
            };
        }

        var envInfo = await CheckPythonEnvironmentAsync();
        if (!envInfo.IsReady)
        {
            return new ExportResult
            {
                ScriptPath = scriptPath,
                Success = false,
                Status = $"GDS generation skipped: {envInfo.StatusMessage}",
                ErrorMessage = envInfo.StatusMessage
            };
        }

        try
        {
            var gdsPath = Path.ChangeExtension(scriptPath, ".gds");
            var (exitCode, output, error) = await ExecutePythonScriptAsync(scriptPath);

            if (exitCode == 0 && File.Exists(gdsPath))
            {
                return new ExportResult
                {
                    ScriptPath = scriptPath,
                    GdsPath = gdsPath,
                    Success = true,
                    Status = $"Script and GDS exported successfully"
                };
            }

            var errorMsg = string.IsNullOrWhiteSpace(error) ? output : error;
            return new ExportResult
            {
                ScriptPath = scriptPath,
                Success = false,
                Status = "GDS generation failed",
                ErrorMessage = $"Python script execution failed (exit code {exitCode}): {errorMsg}"
            };
        }
        catch (Exception ex)
        {
            return new ExportResult
            {
                ScriptPath = scriptPath,
                Success = false,
                Status = "GDS generation failed",
                ErrorMessage = $"Error executing Python: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Resolves the Python command to use: custom path, then discovery service, then fallback.
    /// </summary>
    private async Task<string> ResolvePythonCommandAsync()
    {
        if (!string.IsNullOrEmpty(_customPythonPath))
            return _customPythonPath;

        var discovered = await _pythonDiscovery.FindFirstNazcaPythonPathAsync();
        if (!string.IsNullOrEmpty(discovered))
            return discovered;

        return _launchFactory.ResolveExecutable(DefaultPythonCommand) ?? DefaultPythonCommand;
    }

    /// <summary>
    /// Gets the Python version string.
    /// </summary>
    private async Task<string?> GetPythonVersionAsync()
    {
        try
        {
            var pythonCmd = await ResolvePythonCommandAsync();
            var args = new[] { "--version" };
            if (!_launchFactory.TryBuild(pythonCmd, args, null, null, out var psi, out var launchError))
                return null;

            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError  = true;
            using var process = Process.Start(psi);
            if (process == null) return null;

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask  = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await outputTask;
            await errorTask;

            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
            {
                // Python version output: "Python 3.10.5"
                var parts = output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return parts.Length >= 2 ? parts[1] : output.Trim();
            }
        }
        catch
        {
            // Python not found
        }

        return null;
    }

    /// <summary>
    /// Gets the Nazca version string.
    /// </summary>
    private async Task<string?> GetNazcaVersionAsync()
    {
        try
        {
            var pythonCmd  = await ResolvePythonCommandAsync();
            var checkScript = "import nazca; print(nazca.__version__)";
            var args = new[] { "-c", checkScript };
            if (!_launchFactory.TryBuild(pythonCmd, args, null, null, out var psi, out var launchError))
                return null;

            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError  = true;
            using var process = Process.Start(psi);
            if (process == null) return null;

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask  = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await outputTask;
            await errorTask;

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
    /// Executes a Python script file.
    /// </summary>
    private async Task<(int exitCode, string output, string error)> ExecutePythonScriptAsync(string scriptPath)
    {
        var pythonCmd    = await ResolvePythonCommandAsync();
        var args         = new[] { scriptPath };
        var workingDir   = Path.GetDirectoryName(scriptPath);

        if (!_launchFactory.TryBuild(pythonCmd, args, workingDir, null, out var psi, out var launchError))
            return (-1, string.Empty, launchError ?? "Failed to build process start info");

        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError  = true;
        using var process = Process.Start(psi);
        if (process == null)
            return (-1, string.Empty, "Failed to start Python process");

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask  = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error  = await errorTask;

        return (process.ExitCode, output, error);
    }
}
