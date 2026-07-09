using System.Diagnostics;
using Avalonia.Threading;
using CAP_Core.Components.Core;
using CAP_Core.Solvers.Fdtd;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Solvers;
using CAP.Avalonia.ViewModels.Solvers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.ComponentSettings;

/// <summary>
/// FDTD "Recalculate S-matrix" half of the dialog: instead of loading an
/// S-matrix from a file, compute it from the component's geometry with the
/// selected FDTD backend (local Meep/Docker or the Tidy3D cloud) and feed the
/// result through the same store-and-apply path the file import uses.
/// </summary>
public partial class ComponentSettingsDialogViewModel
{
    private readonly IFdtdSMatrixService? _fdtdService;
    private readonly Func<Component, CancellationToken, Task<FdtdSMatrixRequest?>>? _fdtdRequestFactory;
    private CancellationTokenSource? _recalcCts;

    /// <summary>
    /// Backend picker shared with other FDTD flows. Null when the dialog was
    /// created with a single fixed service (tests / legacy wiring) — the picker
    /// UI hides itself in that case.
    /// </summary>
    public FdtdBackendSelectionViewModel? BackendSelection { get; private set; }

    /// <summary>True while an FDTD recompute is running.</summary>
    [ObservableProperty]
    private bool _isComputing;

    /// <summary>
    /// Live simulation/solver status shown in the dialog (provisioning, running,
    /// energy-conservation summary, or the error/setup hint on failure).
    /// </summary>
    [ObservableProperty]
    private string _solverStatus = string.Empty;

    /// <summary>
    /// True when FDTD recompute is wired up for this dialog instance (solver
    /// service + geometry factory present and a live component is configured).
    /// </summary>
    public bool CanRecalculate =>
        ActiveFdtdService != null && _fdtdRequestFactory != null && _liveComponent != null;

    private bool CanRunRecalculate =>
        CanRecalculate && !IsComputing && !IsImporting && !IsAwaitingCloudConfirmation;

    /// <summary>The solver behind the current backend selection (or the fixed fallback service).</summary>
    private IFdtdSMatrixService? ActiveFdtdService => BackendSelection?.CurrentService ?? _fdtdService;

    /// <summary>Short backend label ("Meep", "Tidy3D") used in status texts and notes.</summary>
    private string SolverLabel => BackendSelection?.CurrentSolverLabel ?? "Meep";

    partial void OnIsComputingChanged(bool value) => RecalculateSMatrixCommand.NotifyCanExecuteChanged();

    /// <summary>
    /// Cancels a running FDTD recompute. Called when the dialog is closed so the
    /// solve (local container or cloud poll) doesn't keep running in the background.
    /// </summary>
    public void CancelRecalculate() => _recalcCts?.Cancel();

    /// <summary>
    /// Recomputes this component's S-matrix from its geometry via the selected
    /// FDTD backend and applies it like an import. Cloud backends that cost
    /// credits pause for an explicit cost confirmation before submitting.
    /// Surfaces the raw solver error on failure — no silent fallback.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunRecalculate))]
    private async Task RecalculateSMatrix()
    {
        var service = ActiveFdtdService;
        if (service == null || _fdtdRequestFactory == null || _liveComponent == null || _storedSMatrices == null)
            return;

        IsComputing = true;
        _recalcCts = new CancellationTokenSource();

        try
        {
            // Fail fast with an actionable message if the backend isn't ready
            // (Docker down / tidy3d missing / API key absent), before exporting
            // geometry or contacting the cloud.
            SolverStatus = $"Checking the FDTD backend ({SolverLabel})…";
            var availability = BackendSelection != null
                ? await BackendSelection.CheckAvailabilityAsync(_recalcCts.Token)
                : await service.CheckAvailabilityAsync(_recalcCts.Token);
            if (!availability.IsAvailable)
            {
                SolverStatus = availability.Message;
                return;
            }

            SolverStatus = "Preparing component geometry…";
            var request = await _fdtdRequestFactory(_liveComponent, _recalcCts.Token);
            if (request == null)
            {
                SolverStatus = "Could not export this component's geometry for FDTD.";
                return;
            }

            // Cloud backends cost credits: estimate first and wait for an explicit
            // confirmation. The solve continues via ConfirmCloudSubmitCommand.
            if (service is IFdtdCostEstimator estimator)
            {
                await PrepareCloudConfirmationAsync(estimator, request, _recalcCts.Token);
                return;
            }

            await ExecuteSolveAsync(service, request, _recalcCts.Token);
        }
        catch (OperationCanceledException)
        {
            SolverStatus = "FDTD recompute cancelled.";
            NotifyCancelled();
        }
        catch (Exception ex)
        {
            SolverStatus = $"FDTD error: {ex.Message}";
            _errorConsole?.LogError($"FDTD recompute crashed for '{_displayName}'", ex);
        }
        finally
        {
            IsComputing = false;
            _recalcCts?.Dispose();
            _recalcCts = null;
            RefreshEntries(notifyChanged: true);
        }
    }

    /// <summary>
    /// Runs the solver and applies the result to the stored/live S-matrices —
    /// the shared tail of both the local path and the confirmed cloud path.
    /// </summary>
    private async Task ExecuteSolveAsync(
        IFdtdSMatrixService service, FdtdSMatrixRequest request, CancellationToken ct)
    {
        var result = await RunWithLiveStatusAsync(service, request, ct);

        if (!result.Success)
        {
            // A user cancel (typically closing the dialog mid-run) comes back as a
            // failed result, not an OperationCanceledException. Closing a window is
            // intentional, not an error, so stay quiet — don't open the error console.
            if (_recalcCts?.IsCancellationRequested == true)
            {
                SolverStatus = "FDTD recompute cancelled.";
                NotifyCancelled();
                return;
            }

            SolverStatus = result.MissingDependency != null
                ? $"FDTD unavailable — '{result.MissingDependency}' is required. {result.Error}"
                : $"FDTD failed: {result.Error}";
            _errorConsole?.LogError($"FDTD recompute failed for '{_displayName}': {result.Error}\n{result.RawStderr}");
            return;
        }

        var note = $"FDTD {SolverLabel} {(result.Is3D ? "3D" : "2D")}";
        var data = FdtdSMatrixConverter.ToComponentSMatrixData(result, note);
        _storedSMatrices![_smatrixKey] = data;

        var applyResult = SMatrixOverrideApplicator.Apply(_liveComponent!, data, _errorConsole);
        SolverStatus = BuildSolverStatus(result, applyResult);
        StatusText = $"Recomputed S-matrix via FDTD ({note}).";
        _notificationService?.ShowSuccess(
            $"S-matrix for '{_displayName}' recomputed via {note} and applied.");
    }

    /// <summary>
    /// Runs the solver while keeping <see cref="SolverStatus"/> alive: a once-per-second
    /// elapsed-time heartbeat (so a long image build / cloud queue never looks frozen)
    /// plus the latest progress line streamed from the solver.
    /// </summary>
    private async Task<FdtdSMatrixResult> RunWithLiveStatusAsync(
        IFdtdSMatrixService service, FdtdSMatrixRequest request, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        string? lastLine = null;
        var baseMsg = service is IFdtdCostEstimator
            ? $"Running FDTD in the {SolverLabel} cloud (upload → queue → run, typically minutes)"
            : "Running FDTD (Meep in Docker). First run builds the solver image (several minutes)";

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
            SolverStatus = lastLine == null
                ? $"{baseMsg} — {stopwatch.Elapsed:m\\:ss} elapsed…"
                : $"FDTD running ({stopwatch.Elapsed:m\\:ss}): {lastLine}";
        SolverStatus = $"{baseMsg}…";
        timer.Start();

        var progress = new Progress<string>(line => lastLine = Shorten(line));
        try
        {
            return await service.SolveAsync(request, progress, ct);
        }
        finally
        {
            timer.Stop();
        }
    }

    /// <summary>
    /// Cancelling (usually by closing the dialog) is intentional, so surface it
    /// as a transient toast on the main window — the dialog's status text may
    /// already be gone and the error console would be far too heavy (#586).
    /// </summary>
    private void NotifyCancelled() =>
        _notificationService?.ShowInfo($"FDTD recompute for '{_displayName}' cancelled.");

    private static string Shorten(string s) => s.Length <= 80 ? s : s[..80] + "…";

    private static string BuildSolverStatus(FdtdSMatrixResult result, ApplyResult? apply)
    {
        var worst = result.EnergySumPerInput.Count > 0 ? result.EnergySumPerInput.Values.Max() : 0.0;
        var energy = result.EnergySumPerInput.Count > 0 ? $" Energy Σ|S|² ≤ {worst:F3} per input." : "";
        var applied = apply == null ? "" : $" Applied {apply.Applied} wavelength(s).";
        return $"FDTD done: {result.Wavelengths.Count} wavelength(s).{energy}{applied}";
    }
}
