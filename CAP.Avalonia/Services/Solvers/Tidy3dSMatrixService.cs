using CAP_Core.Export;
using CAP_Core.Solvers.Fdtd;

namespace CAP.Avalonia.Services.Solvers;

// IFdtdSMatrixService via the Tidy3D cloud FDTD solver (scripts/tidy3d_sparams.py,
// JSON stdin/stdout, same result contract as the Meep bridge). Cloud runs cost
// FlexCredits, so it also implements IFdtdCostEstimator for a pre-submit
// upload-and-estimate. The API key goes to the tidy3d package via SIMCLOUD_APIKEY.
public class Tidy3dSMatrixService : IFdtdSMatrixService, IFdtdCostEstimator
{
    public static readonly TimeSpan DefaultSolveTimeout = TimeSpan.FromMinutes(60);

    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan EstimateTimeout = TimeSpan.FromMinutes(5);

    public const string ApiKeyEnvVar = "SIMCLOUD_APIKEY";

    // Live status lines from the bridge carry this prefix on stderr; anything
    // else on stderr is library noise that must not reach the status line.
    internal const string ProgressPrefix = "LUNIMA_PROGRESS:";

    private readonly Func<string> _pythonExecutableResolver;
    private readonly string _scriptPath;
    private readonly Func<string?> _apiKeyProvider;
    private readonly ProcessLaunchFactory _launchFactory;
    private readonly TimeSpan _timeout;

    // The interpreter resolver is invoked on every run so a newly activated managed
    // environment is picked up without an app restart (same pattern as the mode solver).
    public Tidy3dSMatrixService(
        Func<string> pythonExecutableResolver, string scriptPath, Func<string?> apiKeyProvider,
        ProcessLaunchFactory? launchFactory = null, TimeSpan? timeout = null)
    {
        _pythonExecutableResolver = pythonExecutableResolver ?? throw new ArgumentNullException(nameof(pythonExecutableResolver));
        _scriptPath = scriptPath ?? throw new ArgumentNullException(nameof(scriptPath));
        _apiKeyProvider = apiKeyProvider ?? throw new ArgumentNullException(nameof(apiKeyProvider));
        _launchFactory = launchFactory ?? ProcessLaunchFactory.CreateDefault();
        _timeout = timeout ?? DefaultSolveTimeout;
    }

    public Tidy3dSMatrixService(
        string pythonExecutable, string scriptPath, Func<string?> apiKeyProvider,
        ProcessLaunchFactory? launchFactory = null, TimeSpan? timeout = null)
        : this(() => pythonExecutable ?? throw new ArgumentNullException(nameof(pythonExecutable)),
            scriptPath, apiKeyProvider, launchFactory, timeout)
    {
    }

    public async Task<FdtdSMatrixResult> SolveAsync(
        FdtdSMatrixRequest request, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var hasGds = !string.IsNullOrWhiteSpace(request.GdsPath) && File.Exists(request.GdsPath);
        if (!hasGds && request.Polygons.Count == 0)
            return FdtdSMatrixResult.Fail("No geometry supplied: provide either a GDS file or polygons.");

        if (MissingPrerequisite() is { } missing)
            return FdtdSMatrixResult.Fail(missing.Message, missingDependency: "tidy3d");

        var python = _pythonExecutableResolver();
        var run = await RunScriptAsync(
            python, Tidy3dJsonContract.SerialiseRequest(request, Tidy3dJsonContract.ModeSolve),
            _timeout, ct, progress);

        return run.Outcome switch
        {
            SubprocessJsonRunner.Outcome.StartFailed =>
                FdtdSMatrixResult.Fail($"Could not start Python '{python}': {run.StartError}",
                    missingDependency: "python"),
            // Cancel/timeout only kills the local Python client: an already-submitted
            // cloud batch keeps running and billing. Follow-up: abort the batch
            // server-side (web.Batch.delete / real_cost) before reporting cancel.
            SubprocessJsonRunner.Outcome.Cancelled => FdtdSMatrixResult.Fail(
                "Tidy3D solve was cancelled locally — the cloud batch may still be running " +
                "and billed. Check the Tidy3D dashboard."),
            SubprocessJsonRunner.Outcome.TimedOut => FdtdSMatrixResult.Fail(
                "Tidy3D solve timed out locally — the cloud batch may still be running " +
                "and billed. Check the Tidy3D dashboard."),
            _ => FdtdJsonContract.ParseOutput(run.Stdout, run.Stderr),
        };
    }

    public async Task<FdtdCostEstimate> EstimateCostAsync(
        FdtdSMatrixRequest request, CancellationToken ct = default)
    {
        if (MissingPrerequisite() is { } missing)
            return FdtdCostEstimate.Fail(missing.Message);

        var python = _pythonExecutableResolver();
        var run = await RunScriptAsync(
            python, Tidy3dJsonContract.SerialiseRequest(request, Tidy3dJsonContract.ModeEstimate),
            EstimateTimeout, ct);

        return run.Outcome switch
        {
            SubprocessJsonRunner.Outcome.StartFailed =>
                FdtdCostEstimate.Fail($"Could not start Python '{python}': {run.StartError}"),
            SubprocessJsonRunner.Outcome.Cancelled => FdtdCostEstimate.Fail("Cost estimation was cancelled."),
            SubprocessJsonRunner.Outcome.TimedOut => FdtdCostEstimate.Fail("Cost estimation timed out."),
            _ => Tidy3dJsonContract.ParseEstimate(run.Stdout, run.Stderr),
        };
    }

    public async Task<FdtdAvailability> CheckAvailabilityAsync(CancellationToken ct = default)
    {
        if (MissingPrerequisite() is { } missing)
            return FdtdAvailability.Unavailable(missing.Message, missing.Reason);

        var python = _pythonExecutableResolver();
        var run = await RunScriptAsync(python, "{\"mode\":\"check\"}", CheckTimeout, ct);

        if (run.Outcome == SubprocessJsonRunner.Outcome.StartFailed)
            return FdtdAvailability.Unavailable(
                $"Could not start Python '{python}': {run.StartError}. " +
                "Select a working interpreter in Settings → Python Environments.");
        if (run.Outcome != SubprocessJsonRunner.Outcome.Completed)
            return FdtdAvailability.Unavailable("Tidy3D availability check did not complete.");

        return Tidy3dJsonContract.ParseCheck(run.Stdout, run.Stderr);
    }

    // Fast local gate shared by all entry points: bridge script present and an API
    // key configured. Returns an actionable message plus a machine-readable reason,
    // or null when a run is possible.
    private (string Message, FdtdUnavailableReason Reason)? MissingPrerequisite()
    {
        if (!File.Exists(_scriptPath))
            return ($"Tidy3D bridge script not found: {_scriptPath}", FdtdUnavailableReason.None);
        if (string.IsNullOrWhiteSpace(_apiKeyProvider()))
            return ("No Tidy3D API key configured. Get one at https://tidy3d.simulation.cloud " +
                    "and enter it in Settings → Tidy3D Cloud.", FdtdUnavailableReason.MissingApiKey);
        return null;
    }

    private async Task<SubprocessJsonRunner.RunResult> RunScriptAsync(
        string pythonExecutable, string stdinJson, TimeSpan timeout, CancellationToken ct,
        IProgress<string>? progress = null)
    {
        var env = new Dictionary<string, string> { [ApiKeyEnvVar] = _apiKeyProvider() ?? string.Empty };
        var args = new[] { _scriptPath };
        // The tidy3d web API writes batch/task hdf5 files into the process CWD —
        // run from a per-run writable temp dir (the app install dir is read-only
        // under %ProgramFiles%, and dev bin dirs should not fill up with batch*.hdf5).
        var workDir = Path.Combine(Path.GetTempPath(), "lunima-tidy3d-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            if (!_launchFactory.TryBuild(pythonExecutable, args, workDir, env, out var si, out var error))
                return new SubprocessJsonRunner.RunResult(
                    SubprocessJsonRunner.Outcome.StartFailed, -1, string.Empty, string.Empty, error);

            return await SubprocessJsonRunner.RunAsync(si, stdinJson, timeout, ct,
                onStderrLine: progress == null ? null
                    : line => { if (TryGetProgressText(line, out var text)) progress.Report(text); });
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); }
            catch { /* best-effort: leftover temp files are harmless */ }
        }
    }

    // Only bridge lines carrying ProgressPrefix describe run progress; the rest of
    // stderr is tidy3d/library noise (same filtering idea as the Docker service).
    internal static bool TryGetProgressText(string line, out string text)
    {
        if (line.StartsWith(ProgressPrefix, StringComparison.Ordinal))
        {
            text = line[ProgressPrefix.Length..].Trim();
            return text.Length > 0;
        }
        text = string.Empty;
        return false;
    }
}
