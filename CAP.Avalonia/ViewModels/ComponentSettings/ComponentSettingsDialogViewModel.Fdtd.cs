using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using CAP_Core.Components.Core;
using CAP_Core.Solvers.Fdtd;
using CAP_DataAccess.Persistence.PIR;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Localization;
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
    private readonly IDockerSetupDialogService? _dockerSetupDialog;
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

    /// <summary>True when a backend picker is wired (drives the split-button ▼ part).</summary>
    public bool HasBackendSelection => BackendSelection != null;

    private bool CanRunRecalculate =>
        CanRecalculate && !IsComputing && !IsImporting && !IsAwaitingCloudConfirmation
        && BackendSelection?.IsCurrentBackendUnavailable != true;

    /// <summary>The solver behind the current backend selection (or the fixed fallback service).</summary>
    private IFdtdSMatrixService? ActiveFdtdService => BackendSelection?.CurrentService ?? _fdtdService;

    /// <summary>Short backend label ("Meep", "Tidy3D Cloud") used in status texts and notes.</summary>
    private string SolverLabel => BackendSelection?.CurrentSolverLabel
        ?? FdtdBackendRegistry.SolverLabel(FdtdBackendType.MeepDocker);

    /// <summary>Caption of the recompute button, naming the selected backend.</summary>
    public string RecalculateButtonLabel => string.Format(
        LocalizationService.Instance.Translate("CompSettings.RecalculateWithBackend"), SolverLabel);

    private void SetBackendSelection(FdtdBackendSelectionViewModel? selection)
    {
        BackendSelection = selection;
        if (selection == null)
            return;
        selection.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(FdtdBackendSelectionViewModel.IsCurrentBackendUnavailable))
                RecalculateSMatrixCommand.NotifyCanExecuteChanged();
            if (e.PropertyName is nameof(FdtdBackendSelectionViewModel.CurrentSolverLabel))
                OnPropertyChanged(nameof(RecalculateButtonLabel));
        };
    }

    /// <summary>
    /// Probes the selected backend so a known-bad state (Docker down, no API key)
    /// disables the run button and shows the hint before the user clicks.
    /// </summary>
    public async Task RefreshBackendAvailabilityAsync(CancellationToken ct = default)
    {
        if (BackendSelection != null)
            await BackendSelection.CheckAvailabilityAsync(ct);
    }

    partial void OnIsComputingChanged(bool value) => RecalculateSMatrixCommand.NotifyCanExecuteChanged();

    /// <summary>
    /// Cancels a running FDTD recompute. Called when the dialog is closed so the
    /// solve (local container or cloud poll) doesn't keep running in the background.
    /// </summary>
    public void CancelRecalculate() => _recalcCts?.Cancel();

    /// <summary>
    /// Recomputes this component's S-matrix from its geometry via the selected FDTD
    /// backend and applies it like an import. Backends that cost credits pause for
    /// an explicit cost confirmation before submitting. Surfaces the raw solver
    /// error on failure — no silent fallback.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunRecalculate))]
    private async Task RecalculateSMatrix()
    {
        var service = ActiveFdtdService;
        if (service == null || _fdtdRequestFactory == null || _liveComponent == null || _storedSMatrices == null)
        {
            // Trips only when the dialog was never Configure()d — never fail silently.
            SolverStatus = LocalizationService.Instance.Translate("CompSettings.FdtdNotAvailable");
            return;
        }

        IsComputing = true;
        _recalcCts = new CancellationTokenSource();

        try
        {
            // Fail fast with an actionable message if the backend isn't ready, before
            // exporting geometry / building images / contacting the cloud.
            SolverStatus = string.Format(
                LocalizationService.Instance.Translate("CompSettings.CheckingFdtd"), SolverLabel);
            var availability = BackendSelection != null
                ? await BackendSelection.CheckAvailabilityAsync(_recalcCts.Token)
                : await service.CheckAvailabilityAsync(_recalcCts.Token);
            if (!availability.IsAvailable)
            {
                SolverStatus = availability.Message;
                // Cloud backends have no local guided setup — the amber hint under the
                // button row (and its settings link) tells the user what to fix.
                if (service is IFdtdCostEstimator || _dockerSetupDialog == null)
                    return;
                // Guided setup (issue #649): open the "Set up FDTD" dialog with
                // platform-specific install/start guidance and a re-check button.
                // Headless/test consumers without the dialog service keep the
                // plain error-string behaviour above.
                var ready = await _dockerSetupDialog.ShowAsync(
                    availability, ct => service.CheckAvailabilityAsync(ct));
                if (!ready)
                    return;
                // The guided dialog's re-check proved readiness — clear the failed
                // probe state so the run button doesn't stay disabled.
                BackendSelection?.ClearAvailabilityState();
                SolverStatus = LocalizationService.Instance.Translate("CompSettings.DockerReady");
            }

            SolverStatus = LocalizationService.Instance.Translate("CompSettings.PreparingGeometry");
            FdtdSMatrixRequest? request;
            try
            {
                request = await _fdtdRequestFactory(_liveComponent, _recalcCts.Token);
            }
            catch (InvalidOperationException ex)
            {
                // The factory already built an actionable message — show it, not a generic status.
                SolverStatus = ex.Message;
                _errorConsole?.LogError(
                    $"FDTD geometry export failed for '{_displayName}': {ex.Message}");
                return;
            }
            if (request == null)
            {
                SolverStatus = LocalizationService.Instance.Translate("CompSettings.CouldNotExportGeometry");
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
            SolverStatus = LocalizationService.Instance.Translate("CompSettings.RecomputeCancelled");
            NotifyCancelled();
        }
        catch (Exception ex)
        {
            SolverStatus = string.Format(LocalizationService.Instance.Translate("CompSettings.FdtdError"), ex.Message);
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
    /// Runs the solver and applies the result to the stored/live S-matrices — the
    /// shared tail of both the local path and the confirmed cloud path.
    /// </summary>
    /// <param name="provenanceLabel">
    /// Solver label captured at estimate time for a confirmed cloud run — the
    /// provenance note must name the backend the run was estimated/submitted on,
    /// not whatever is selected by the time it finishes. Null = use the live label.
    /// </param>
    private async Task ExecuteSolveAsync(
        IFdtdSMatrixService service, FdtdSMatrixRequest request, CancellationToken ct,
        string? provenanceLabel = null)
    {
        var result = await RunWithLiveStatusAsync(service, request, ct);

        if (!result.Success)
        {
            // A user cancel (typically closing the dialog mid-run) comes back as a
            // failed result, not an OperationCanceledException. Closing a window is
            // intentional, not an error, so stay quiet — don't open the error console.
            if (_recalcCts?.IsCancellationRequested == true)
            {
                SolverStatus = LocalizationService.Instance.Translate("CompSettings.RecomputeCancelled");
                NotifyCancelled();
                return;
            }

            SolverStatus = result.MissingDependency != null
                ? string.Format(
                    LocalizationService.Instance.Translate("CompSettings.FdtdUnavailableDependency"),
                    result.MissingDependency, result.Error)
                : string.Format(LocalizationService.Instance.Translate("CompSettings.FdtdFailed"), result.Error);
            _errorConsole?.LogError($"FDTD recompute failed for '{_displayName}': {result.Error}\n{result.RawStderr}");
            return;
        }

        var note = string.Format(
            LocalizationService.Instance.Translate("CompSettings.FdtdProvenance"),
            provenanceLabel ?? SolverLabel, result.Is3D ? "3D" : "2D");
        var data = FdtdSMatrixConverter.ToComponentSMatrixData(result, note);
        _storedSMatrices![_smatrixKey] = data;

        var applyResult = SMatrixOverrideApplicator.Apply(_liveComponent!, data, _errorConsole);
        var staleNm = FindStaleWavelengths(_liveComponent!, data);
        if (staleNm.Count > 0)
        {
            _errorConsole?.LogWarning(
                $"FDTD recompute for '{_displayName}' did not cover {staleNm.Count} previously defined " +
                $"wavelength(s): {FormatNm(staleNm)} — those entries keep their old (PDK-default) values.");
        }

        // Issue #580 E: when the instance geometry matches the template draft,
        // the caller-provided sink promotes the result to the template-scoped
        // (user-global) override so every instance of the type inherits it.
        var propagated = _propagateToTemplate?.Invoke(data) == true;
        SolverStatus = BuildSolverStatus(result, applyResult, propagated, staleNm);
        StatusText = propagated
            ? string.Format(LocalizationService.Instance.Translate("CompSettings.RecomputedPropagated"), note)
            : string.Format(LocalizationService.Instance.Translate("CompSettings.Recomputed"), note);
        _notificationService?.ShowSuccess(propagated
            ? string.Format(
                LocalizationService.Instance.Translate("CompSettings.NotifyRecomputedPropagated"), _displayName, note)
            : string.Format(
                LocalizationService.Instance.Translate("CompSettings.NotifyRecomputed"), _displayName, note));
    }

    /// <summary>
    /// Runs the solver while keeping <see cref="SolverStatus"/> alive: a once-per-second
    /// elapsed-time heartbeat (so the long image build / FDTD run never looks frozen)
    /// plus the latest progress line streamed from the solver.
    /// </summary>
    private async Task<FdtdSMatrixResult> RunWithLiveStatusAsync(
        IFdtdSMatrixService service, FdtdSMatrixRequest request, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        string? lastLine = null;
        var baseMsg = service is IFdtdCostEstimator
            ? string.Format(
                LocalizationService.Instance.Translate("CompSettings.FdtdRunBaseMessageCloud"), SolverLabel)
            : LocalizationService.Instance.Translate("CompSettings.FdtdRunBaseMessage");

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
            SolverStatus = lastLine == null
                ? string.Format(
                    LocalizationService.Instance.Translate("CompSettings.FdtdElapsed"),
                    baseMsg, stopwatch.Elapsed.ToString(@"m\:ss"))
                : string.Format(
                    LocalizationService.Instance.Translate("CompSettings.FdtdRunning"),
                    stopwatch.Elapsed.ToString(@"m\:ss"), lastLine);
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
        _notificationService?.ShowInfo(string.Format(
            LocalizationService.Instance.Translate("CompSettings.NotifyRecomputeCancelled"), _displayName));

    private static string Shorten(string s) => s.Length <= 80 ? s : s[..80] + "…";

    /// <summary>
    /// Wavelengths (nm) still in the component's effective S-matrix map that this
    /// FDTD run did NOT recompute — they keep their old (typically PDK-default)
    /// values, so the user must be told about them (#582).
    /// </summary>
    private static IReadOnlyList<int> FindStaleWavelengths(Component component, ComponentSMatrixData data) =>
        component.WaveLengthToSMatrixMap.Keys
            .Where(nm => !data.Wavelengths.ContainsKey(nm.ToString(CultureInfo.InvariantCulture)))
            .OrderBy(nm => nm)
            .ToList();

    private static string FormatNm(IReadOnlyList<int> wavelengthsNm) =>
        string.Join(", ", wavelengthsNm.Select(nm => nm.ToString(CultureInfo.InvariantCulture))) + " nm";

    private static string BuildSolverStatus(
        FdtdSMatrixResult result, ApplyResult? apply, bool propagatedToTemplate, IReadOnlyList<int> staleNm)
    {
        var worst = result.EnergySumPerInput.Count > 0 ? result.EnergySumPerInput.Values.Max() : 0.0;
        var energy = result.EnergySumPerInput.Count > 0
            ? string.Format(LocalizationService.Instance.Translate("CompSettings.EnergySummary"), worst)
            : "";
        var applied = apply == null
            ? ""
            : string.Format(LocalizationService.Instance.Translate("CompSettings.AppliedWavelengths"), apply.Applied);
        var scope = propagatedToTemplate
            ? LocalizationService.Instance.Translate("CompSettings.AppliedAllInstances")
            : "";
        var stale = staleNm.Count == 0
            ? ""
            : string.Format(LocalizationService.Instance.Translate("CompSettings.StaleNotCovered"), FormatNm(staleNm));
        return string.Format(
            LocalizationService.Instance.Translate("CompSettings.FdtdDone"),
            result.Wavelengths.Count, energy, applied, scope, stale);
    }
}
