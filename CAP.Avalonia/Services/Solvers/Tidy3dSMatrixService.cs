using CAP_Core.Export;
using CAP_Core.Solvers.Fdtd;

namespace CAP.Avalonia.Services.Solvers;

/// <summary>
/// Implements <see cref="IFdtdSMatrixService"/> by submitting the component to the
/// Tidy3D cloud FDTD solver via <c>scripts/tidy3d_sparams.py</c> (JSON stdin/stdout,
/// same result contract as the Meep bridge). Cloud runs cost FlexCredits, so the
/// service also implements <see cref="IFdtdCostEstimator"/> for a pre-submit
/// upload-and-estimate. The API key is injected via the <c>SIMCLOUD_APIKEY</c>
/// environment variable, which the tidy3d package honours natively.
/// </summary>
public class Tidy3dSMatrixService : IFdtdSMatrixService, IFdtdCostEstimator
{
    /// <summary>Default per-solve timeout. Cloud queue + run can take many minutes.</summary>
    public static readonly TimeSpan DefaultSolveTimeout = TimeSpan.FromMinutes(60);

    /// <summary>Timeout for the quick availability probe (imports tidy3d, no network).</summary>
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Timeout for upload-and-estimate (uploads the simulation, no run).</summary>
    public static readonly TimeSpan EstimateTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Environment variable the tidy3d package reads its API key from.</summary>
    public const string ApiKeyEnvVar = "SIMCLOUD_APIKEY";

    private readonly string _pythonExecutable;
    private readonly string _scriptPath;
    private readonly Func<string?> _apiKeyProvider;
    private readonly ProcessLaunchFactory _launchFactory;
    private readonly TimeSpan _timeout;

    /// <summary>Initializes the service.</summary>
    /// <param name="pythonExecutable">Python 3 interpreter with the tidy3d package.</param>
    /// <param name="scriptPath">Absolute path to <c>tidy3d_sparams.py</c>.</param>
    /// <param name="apiKeyProvider">
    /// Returns the current Tidy3D API key (read lazily so a key entered in Settings
    /// takes effect without restarting). Null/empty means "not configured".
    /// </param>
    /// <param name="launchFactory">Cross-platform process-launch factory (CLAUDE.md rule).</param>
    /// <param name="timeout">Optional per-solve timeout.</param>
    public Tidy3dSMatrixService(
        string pythonExecutable, string scriptPath, Func<string?> apiKeyProvider,
        ProcessLaunchFactory? launchFactory = null, TimeSpan? timeout = null)
    {
        _pythonExecutable = pythonExecutable ?? throw new ArgumentNullException(nameof(pythonExecutable));
        _scriptPath = scriptPath ?? throw new ArgumentNullException(nameof(scriptPath));
        _apiKeyProvider = apiKeyProvider ?? throw new ArgumentNullException(nameof(apiKeyProvider));
        _launchFactory = launchFactory ?? ProcessLaunchFactory.CreateDefault();
        _timeout = timeout ?? DefaultSolveTimeout;
    }

    /// <inheritdoc/>
    public async Task<FdtdSMatrixResult> SolveAsync(
        FdtdSMatrixRequest request, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var hasGds = !string.IsNullOrWhiteSpace(request.GdsPath) && File.Exists(request.GdsPath);
        if (!hasGds && request.Polygons.Count == 0)
            return FdtdSMatrixResult.Fail("No geometry supplied: provide either a GDS file or polygons.");

        if (MissingPrerequisite() is { } missing)
            return FdtdSMatrixResult.Fail(missing, missingDependency: "tidy3d");

        var run = await RunScriptAsync(
            Tidy3dJsonContract.SerialiseRequest(request, Tidy3dJsonContract.ModeSolve),
            _timeout, ct, progress);

        return run.Outcome switch
        {
            SubprocessJsonRunner.Outcome.StartFailed =>
                FdtdSMatrixResult.Fail($"Could not start Python '{_pythonExecutable}': {run.StartError}",
                    missingDependency: "python"),
            SubprocessJsonRunner.Outcome.Cancelled => FdtdSMatrixResult.Fail("Tidy3D solve was cancelled."),
            SubprocessJsonRunner.Outcome.TimedOut => FdtdSMatrixResult.Fail("Tidy3D solve timed out."),
            _ => FdtdJsonContract.ParseOutput(run.Stdout, run.Stderr),
        };
    }

    /// <inheritdoc/>
    public async Task<FdtdCostEstimate> EstimateCostAsync(
        FdtdSMatrixRequest request, CancellationToken ct = default)
    {
        if (MissingPrerequisite() is { } missing)
            return FdtdCostEstimate.Fail(missing);

        var run = await RunScriptAsync(
            Tidy3dJsonContract.SerialiseRequest(request, Tidy3dJsonContract.ModeEstimate),
            EstimateTimeout, ct);

        return run.Outcome switch
        {
            SubprocessJsonRunner.Outcome.StartFailed =>
                FdtdCostEstimate.Fail($"Could not start Python '{_pythonExecutable}': {run.StartError}"),
            SubprocessJsonRunner.Outcome.Cancelled => FdtdCostEstimate.Fail("Cost estimation was cancelled."),
            SubprocessJsonRunner.Outcome.TimedOut => FdtdCostEstimate.Fail("Cost estimation timed out."),
            _ => Tidy3dJsonContract.ParseEstimate(run.Stdout, run.Stderr),
        };
    }

    /// <inheritdoc/>
    public async Task<FdtdAvailability> CheckAvailabilityAsync(CancellationToken ct = default)
    {
        if (MissingPrerequisite() is { } missing)
            return FdtdAvailability.Unavailable(missing);

        var run = await RunScriptAsync("{\"mode\":\"check\"}", CheckTimeout, ct);

        if (run.Outcome == SubprocessJsonRunner.Outcome.StartFailed)
            return FdtdAvailability.Unavailable(
                $"Could not start Python '{_pythonExecutable}': {run.StartError}. " +
                "Select a working interpreter in Settings → Python Environments.");
        if (run.Outcome != SubprocessJsonRunner.Outcome.Completed)
            return FdtdAvailability.Unavailable("Tidy3D availability check did not complete.");

        return Tidy3dJsonContract.ParseCheck(run.Stdout, run.Stderr);
    }

    /// <summary>
    /// Fast local prerequisite check shared by all entry points: bridge script
    /// present and an API key configured. Returns an actionable message, or null
    /// when everything needed to try a run is in place.
    /// </summary>
    private string? MissingPrerequisite()
    {
        if (!File.Exists(_scriptPath))
            return $"Tidy3D bridge script not found: {_scriptPath}";
        if (string.IsNullOrWhiteSpace(_apiKeyProvider()))
            return "No Tidy3D API key configured. Get one at https://tidy3d.simulation.cloud " +
                   "and enter it in Settings → Tidy3D Cloud.";
        return null;
    }

    private async Task<SubprocessJsonRunner.RunResult> RunScriptAsync(
        string stdinJson, TimeSpan timeout, CancellationToken ct, IProgress<string>? progress = null)
    {
        var env = new Dictionary<string, string> { [ApiKeyEnvVar] = _apiKeyProvider() ?? string.Empty };
        var args = new[] { _scriptPath };
        if (!_launchFactory.TryBuild(_pythonExecutable, args, null, env, out var si, out var error))
            return new SubprocessJsonRunner.RunResult(
                SubprocessJsonRunner.Outcome.StartFailed, -1, string.Empty, string.Empty, error);

        return await SubprocessJsonRunner.RunAsync(si, stdinJson, timeout, ct,
            onStderrLine: progress == null ? null : line => progress.Report(line));
    }
}
