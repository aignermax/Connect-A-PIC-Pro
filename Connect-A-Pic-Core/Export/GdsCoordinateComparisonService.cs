using System.Diagnostics;
using System.Text.Json;

namespace CAP_Core.Export;

/// <summary>
/// Core service for comparing two GDS coordinate JSON files using the
/// scripts/compare_gds_coords.py tool.  Reports exact deviations in micrometres
/// so fabrication-blocking alignment bugs can be confirmed or refuted.
/// </summary>
public class GdsCoordinateComparisonService
{
    private readonly ProcessLaunchFactory _launchFactory;

    /// <summary>Result of a GDS coordinate comparison run.</summary>
    public class ComparisonResult
    {
        /// <summary>True if all elements are within tolerance.</summary>
        public bool Passed { get; init; }

        /// <summary>Maximum centroid deviation across all elements in µm.</summary>
        public double MaxDeviationUm { get; init; }

        /// <summary>Root-mean-square centroid deviation in µm.</summary>
        public double RmsDeviationUm { get; init; }

        /// <summary>Tolerance used for the comparison in µm.</summary>
        public double ToleranceUm { get; init; }

        /// <summary>Full human-readable report from the Python script stdout.</summary>
        public string RawOutput { get; init; } = string.Empty;

        /// <summary>Error text from stderr (populated on failure).</summary>
        public string ErrorOutput { get; init; } = string.Empty;

        /// <summary>
        /// Formatted status line for UI display, e.g. "PASS — max Δ 0.000001 µm".
        /// </summary>
        public string StatusText => Passed
            ? $"PASS — max \u0394 {MaxDeviationUm:F6} \u03bcm"
            : $"FAIL — max \u0394 {MaxDeviationUm:F6} \u03bcm";
    }

    private string? _customPythonPath;

    /// <summary>
    /// Initializes the service with the required process launch factory.
    /// </summary>
    /// <param name="launchFactory">Factory used to build <see cref="ProcessStartInfo"/> instances.</param>
    public GdsCoordinateComparisonService(ProcessLaunchFactory? launchFactory = null)
    {
        _launchFactory = launchFactory ?? ProcessLaunchFactory.CreateDefault();
    }

    /// <summary>
    /// Sets a custom Python executable path.  When null the system default is used.
    /// </summary>
    /// <param name="pythonPath">Absolute path to Python executable, or null.</param>
    public void SetCustomPythonPath(string? pythonPath) => _customPythonPath = pythonPath;

    /// <summary>
    /// Runs <c>compare_gds_coords.py</c> on two JSON coordinate files.
    /// </summary>
    /// <param name="referenceJsonPath">Path to the reference coordinate JSON.</param>
    /// <param name="systemJsonPath">Path to the system coordinate JSON to compare.</param>
    /// <param name="scriptPath">
    /// Absolute path to compare_gds_coords.py.
    /// When null, <see cref="FindDefaultScriptPath"/> is used.
    /// </param>
    /// <returns>Structured comparison result.</returns>
    public async Task<ComparisonResult> CompareAsync(
        string referenceJsonPath,
        string systemJsonPath,
        string? scriptPath = null)
    {
        var resolvedScript = scriptPath ?? FindDefaultScriptPath();
        if (resolvedScript == null || !File.Exists(resolvedScript))
            return ErrorResult($"Script not found: {resolvedScript ?? "(null)"}");

        if (!File.Exists(referenceJsonPath))
            return ErrorResult($"Reference JSON not found: {referenceJsonPath}");

        if (!File.Exists(systemJsonPath))
            return ErrorResult($"System JSON not found: {systemJsonPath}");

        var reportPath = Path.Combine(Path.GetTempPath(), $"cap_comparison_{Guid.NewGuid():N}.json");
        var scriptArgs = new[] { resolvedScript, referenceJsonPath, systemJsonPath, reportPath };

        if (!_launchFactory.TryBuild("python3", scriptArgs, Path.GetDirectoryName(resolvedScript), null, out var psi, out var launchError))
            return ErrorResult($"Failed to build process: {launchError}");

        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;

        var (exitCode, stdout, stderr) = await RunCommandAsync(psi);

        return ParseResult(exitCode, stdout, stderr, reportPath);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ComparisonResult ParseResult(int exitCode, string stdout, string stderr, string reportPath)
    {
        try
        {
            if (File.Exists(reportPath))
            {
                var json = File.ReadAllText(reportPath);
                File.Delete(reportPath);
                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                return new ComparisonResult
                {
                    Passed = root.GetProperty("passed").GetBoolean(),
                    MaxDeviationUm = root.GetProperty("max_deviation_um").GetDouble(),
                    RmsDeviationUm = root.GetProperty("rms_deviation_um").GetDouble(),
                    ToleranceUm = root.GetProperty("tolerance_um").GetDouble(),
                    RawOutput = stdout,
                    ErrorOutput = stderr
                };
            }
        }
        catch (Exception ex)
        {
            return ErrorResult($"Failed to parse report: {ex.Message}\nStdout: {stdout}\nStderr: {stderr}");
        }

        return new ComparisonResult
        {
            Passed = exitCode == 0,
            RawOutput = stdout,
            ErrorOutput = stderr
        };
    }

    private static ComparisonResult ErrorResult(string message) => new()
    {
        Passed = false,
        RawOutput = message,
        ErrorOutput = message
    };

    private static async Task<(int exitCode, string stdout, string stderr)> RunCommandAsync(
        ProcessStartInfo startInfo, int timeoutMs = 30_000)
    {
        using var process = Process.Start(startInfo)!;

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        await Task.WhenAny(
            process.WaitForExitAsync(),
            Task.Delay(timeoutMs));

        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            return (-1, string.Empty, $"Timeout after {timeoutMs / 1000}s");
        }

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    /// <summary>
    /// Searches for <c>scripts/compare_gds_coords.py</c> by walking up from the
    /// current assembly directory.  Returns null if not found.
    /// </summary>
    public static string? FindDefaultScriptPath()
    {
        const string RelativePath = "scripts/compare_gds_coords.py";
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, RelativePath);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        return null;
    }
}
