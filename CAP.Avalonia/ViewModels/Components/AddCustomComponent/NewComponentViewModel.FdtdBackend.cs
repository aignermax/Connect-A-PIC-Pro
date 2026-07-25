using System;
using System.Threading;
using System.Threading.Tasks;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.Services.Solvers;
using CAP.Avalonia.ViewModels.Solvers;
using CAP_Core.Solvers.Fdtd;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Components.AddCustomComponent;

public partial class NewComponentViewModel
{
    private FdtdSMatrixRequest? _pendingCloudRequest;
    private IFdtdSMatrixService? _pendingCloudService;

    // Solver label captured at ESTIMATE time next to the pending service: a backend
    // switch in another window between estimate and confirm must not relabel the run.
    private string? _pendingCloudLabel;

    /// <summary>
    /// FDTD backend picker (Meep local / Tidy3D cloud). Null when the editor was
    /// created without a backend registry (tests / legacy wiring) — the picker UI
    /// hides itself in that case.
    /// </summary>
    public FdtdBackendSelectionViewModel? BackendSelection { get; private set; }

    public bool HasBackendSelection => BackendSelection != null;

    /// <summary>True while the "this run costs credits — submit?" panel is shown.</summary>
    [ObservableProperty]
    private bool _isAwaitingCloudConfirmation;

    /// <summary>Estimated cost text in the confirmation panel (or the honest "unavailable" note).</summary>
    [ObservableProperty]
    private string _cloudCostText = string.Empty;

    private IFdtdSMatrixService? ActiveFdtdService => BackendSelection?.CurrentService ?? _fdtd;

    private string SolverLabel => BackendSelection?.CurrentSolverLabel
        ?? FdtdBackendRegistry.SolverLabel(FdtdBackendType.MeepDocker);

    /// <summary>Caption of the compute button, naming the selected backend.</summary>
    public string ComputeButtonLabel => string.Format(
        LocalizationService.Instance.Translate("NewComponent.ComputeWithBackend"), SolverLabel);

    private bool CanRunCompute =>
        !IsBusy && !IsAwaitingCloudConfirmation && BackendSelection?.IsCurrentBackendUnavailable != true;

    private void InitFdtdBackendSelection(FdtdBackendRegistry? registry, IUrlLauncher? urlLauncher)
    {
        if (registry == null)
            return;
        BackendSelection = new FdtdBackendSelectionViewModel(registry, urlLauncher);
        BackendSelection.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(FdtdBackendSelectionViewModel.IsCurrentBackendUnavailable))
                ComputeSMatrixCommand.NotifyCanExecuteChanged();
            if (e.PropertyName is nameof(FdtdBackendSelectionViewModel.CurrentSolverLabel))
                OnPropertyChanged(nameof(ComputeButtonLabel));
        };
    }

    /// <summary>Releases the backend picker's registry subscription (host calls this on window close).</summary>
    public void Dispose() => BackendSelection?.Dispose();

    /// <summary>
    /// Probes the selected backend so a known-bad state (Docker down, no API key)
    /// disables the compute button and shows the hint before the user clicks.
    /// </summary>
    public async Task RefreshBackendAvailabilityAsync(CancellationToken ct = default)
    {
        if (BackendSelection != null)
            await BackendSelection.CheckAvailabilityAsync(ct);
    }

    partial void OnIsAwaitingCloudConfirmationChanged(bool value)
    {
        ComputeSMatrixCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Estimates the cloud cost of <paramref name="request"/> and switches the editor
    /// into the awaiting-confirmation state. Nothing is solved yet.
    /// </summary>
    private async Task PrepareCloudConfirmationAsync(
        IFdtdCostEstimator estimator, FdtdSMatrixRequest request, CancellationToken ct)
    {
        StatusText = string.Format(
            LocalizationService.Instance.Translate("CompSettings.EstimatingCloudCost"), SolverLabel);
        var estimate = await estimator.EstimateCostAsync(request, ct);

        CloudCostText = estimate.Success
            ? string.Format(
                LocalizationService.Instance.Translate("CompSettings.CloudCostEstimate"),
                estimate.EstimatedCredits, estimate.SimulationCount)
            : string.Format(
                LocalizationService.Instance.Translate("CompSettings.CloudCostUnavailable"), estimate.Error);

        _pendingCloudRequest = request;
        _pendingCloudService = (IFdtdSMatrixService)estimator;
        _pendingCloudLabel = SolverLabel;
        StatusText = string.Format(
            LocalizationService.Instance.Translate("CompSettings.CloudConfirmPrompt"), SolverLabel);
        IsAwaitingCloudConfirmation = true;
    }

    [RelayCommand]
    private async Task ConfirmCloudSubmit()
    {
        var service = _pendingCloudService;
        var request = _pendingCloudRequest;
        var cloudLabel = _pendingCloudLabel;
        ClearPendingCloudJob();
        if (service == null || request == null)
            return;

        IsBusy = true;
        _computeCts = new CancellationTokenSource();
        try
        {
            await ExecuteSolveAsync(service, request, _computeCts.Token, cloudLabel);
        }
        catch (OperationCanceledException)
        {
            _computedModel = null;
            // A cancelled CLOUD wait can leave a submitted job running and billing —
            // say so instead of claiming nothing happened.
            StatusText = LocalizationService.Instance.Translate(
                service is IFdtdCostEstimator
                    ? "NewComp.SMatrixComputationCancelledCloud"
                    : "NewComp.SMatrixComputationCancelled");
        }
        catch (Exception ex)
        {
            // Mirror the settings dialog's twin: without this the AsyncRelayCommand
            // swallows the fault and the submit looks like it did nothing.
            _computedModel = null;
            StatusText = string.Format(LocalizationService.Instance.Translate("CompSettings.FdtdError"), ex.Message);
            _errorConsole?.LogError($"FDTD cloud submit crashed for '{ComponentName}': {ex.Message}", ex);
        }
        finally
        {
            IsBusy = false;
            _computeCts?.Dispose();
            _computeCts = null;
            RefreshSMatrixEntries();
        }
    }

    [RelayCommand]
    private void CancelCloudSubmit()
    {
        ClearPendingCloudJob();
        StatusText = LocalizationService.Instance.Translate("CompSettings.CloudSubmitCancelled");
    }

    private void ClearPendingCloudJob()
    {
        _pendingCloudRequest = null;
        _pendingCloudService = null;
        _pendingCloudLabel = null;
        IsAwaitingCloudConfirmation = false;
    }
}
