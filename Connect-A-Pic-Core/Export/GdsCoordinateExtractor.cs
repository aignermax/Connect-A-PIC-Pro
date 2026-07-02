using System.Diagnostics;

namespace CAP_Core.Export;

/// <summary>
/// Runs the Python gdspy coordinate extraction script on a GDS file
/// and returns structured JSON output for numerical comparison.
/// Supports debugging coordinate mismatches between component pins
/// and waveguide placements (issue #329).
/// </summary>
public class GdsCoordinateExtractor
{
    private const string ScriptRelativePath = "scripts/extract_gds_coords.py";
    private const int ExtractionTimeoutMs = 30_000;

    private readonly ProcessLaunchFactory _launchFactory;
    private string? _customPythonPath;

    /// <summary>
    /// Initializes a new instance of <see cref="GdsCoordinateExtractor"/>.
    /// </summary>
    /// <param name="launchFactory">Factory used to build process start info.</param>
    public GdsCoordinateExtractor(ProcessLaunchFactory? launchFactory = null)
    {
        _launchFactory = launchFactory ?? ProcessLaunchFactory.CreateDefault();
    }

    /// <summary>Result of a coordinate extraction operation.</summary>
    public class ExtractionResult
    {
        /// <summary>True if extraction completed successfully.</summary>
        public bool Success { get; init; }

        /// <summary>Path to the output JSON file.</summary>
        public string? JsonOutputPath { get; init; }

        /// <summary>Raw JSON string with extracted coordinates.</summary>
        public string? JsonContent { get; init; }

        /// <summary>Error message if extraction failed.</summary>
        public string? ErrorMessage { get; init; }

        /// <summary>Status message for UI display.</summary>
        public string Status { get; init; } = string.Empty;
    }

    /// <summary>Sets a custom Python executable path to use instead of the system default.</summary>
    /// <param name="pythonPath">Path to Python executable, or null to use system default.</param>
    public void SetCustomPythonPath(string? pythonPath)
    {
        _customPythonPath = pythonPath;
    }

    /// <summary>
    /// Extracts all polygon and path coordinates from a GDS file using the
    /// scripts/extract_gds_coords.py Python script.
    /// </summary>
    /// <param name="gdsPath">Path to the GDS file to analyze.</param>
    /// <param name="outputJsonPath">Output JSON path; defaults to .coords.json alongside the GDS file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Extraction result containing the JSON coordinate data.</returns>
    public async Task<ExtractionResult> ExtractAsync(
        string gdsPath,
        string? outputJsonPath = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(gdsPath))
            return Failure($"GDS file not found: {gdsPath}");

        var scriptPath = FindScriptPath();
        if (scriptPath == null)
            return Failure($"Extraction script not found. Expected at: {ScriptRelativePath}");

        var jsonPath = outputJsonPath ?? Path.ChangeExtension(gdsPath, ".coords.json");

        try
        {
            var command = string.IsNullOrEmpty(_customPythonPath)
                ? _launchFactory.ResolveExecutable("python3") ?? "python3"
                : _customPythonPath;

            var args = new[] { scriptPath, gdsPath, jsonPath };
            var workingDir = Path.GetDirectoryName(scriptPath);

            if (!_launchFactory.TryBuild(command, args, workingDir, null, out var psi, out var launchError))
                return Failure($"Failed to build process: {launchError}");

            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null.");

            // Linked CTS + CancelAfter (same idiom as PdkNazcaParserService): the timeout timer
            // is disposed with the CTS the moment the method returns, so the common fast-success
            // path doesn't leave a 30s timer armed (one leaked timer per call).
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ExtractionTimeoutMs);
            var linkedToken = timeoutCts.Token;

            var stdoutTask = process.StandardOutput.ReadToEndAsync(linkedToken);
            var stderrTask = process.StandardError.ReadToEndAsync(linkedToken);

            try
            {
                await process.WaitForExitAsync(linkedToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                return Failure($"Timeout after {ExtractionTimeoutMs / 1000}s");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                var msg = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                return Failure($"Script failed (exit {process.ExitCode}): {msg}");
            }

            if (!File.Exists(jsonPath))
                return Failure("Script ran but no output JSON was created.");

            var content = await File.ReadAllTextAsync(jsonPath, cancellationToken);
            return new ExtractionResult
            {
                Success = true,
                JsonOutputPath = jsonPath,
                JsonContent = content,
                Status = $"Extracted to {Path.GetFileName(jsonPath)}"
            };
        }
        catch (OperationCanceledException)
        {
            return Failure("Extraction was cancelled.");
        }
        catch (Exception ex)
        {
            return Failure($"Error running extraction script: {ex.Message}");
        }
    }

    /// <summary>Checks whether Python is available for running the extraction script.</summary>
    /// <returns>True if Python is available.</returns>
    public async Task<bool> IsPythonAvailableAsync()
    {
        try
        {
            var command = string.IsNullOrEmpty(_customPythonPath)
                ? _launchFactory.ResolveExecutable("python3") ?? "python3"
                : _customPythonPath;

            if (!_launchFactory.TryBuild(command, new[] { "--version" }, null, null, out var psi, out _))
                return false;

            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            using var process = Process.Start(psi);
            if (process == null) return false;

            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves the path to the extraction script.
    /// Searches relative to the executable, current directory, and walks up
    /// the directory tree to locate the repository root (useful in test contexts).
    /// </summary>
    public static string? FindScriptPath()
    {
        var candidateDirs = new[]
        {
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory()
        };

        foreach (var dir in candidateDirs)
        {
            // Direct match
            var direct = Path.Combine(dir, ScriptRelativePath);
            if (File.Exists(direct))
                return direct;

            // Walk up to find a directory containing the Scripts folder
            var current = dir;
            for (var i = 0; i < 8; i++)
            {
                var candidate = Path.Combine(current, ScriptRelativePath);
                if (File.Exists(candidate))
                    return candidate;
                var parent = Path.GetDirectoryName(current);
                if (parent == null || parent == current)
                    break;
                current = parent;
            }
        }

        return null;
    }

    private static ExtractionResult Failure(string message) =>
        new() { Success = false, ErrorMessage = message, Status = message };
}
