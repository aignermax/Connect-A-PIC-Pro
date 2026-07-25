using CAP.Avalonia.Services.Localization;
using CAP_Core.Solvers.Fdtd;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.ComponentSettings;

/// <summary>
/// Cloud-cost confirmation half of the FDTD recompute flow: backends that cost
/// credits (Tidy3D) pause after the cost estimate and wait for an explicit
/// user confirmation before anything is submitted to the cloud.
/// </summary>
public partial class ComponentSettingsDialogViewModel
{
    private FdtdSMatrixRequest? _pendingCloudRequest;
    private IFdtdSMatrixService? _pendingCloudService;

    // Solver label captured at ESTIMATE time next to the pending service: a backend
    // switch in another window between estimate and confirm must not relabel the run.
    private string? _pendingCloudLabel;

    /// <summary>
    /// True while the dialog shows the "this run costs credits — submit?" panel.
    /// The recompute button stays disabled until confirmed or dismissed.
    /// </summary>
    [ObservableProperty]
    private bool _isAwaitingCloudConfirmation;

    /// <summary>
    /// Estimated cost text shown in the confirmation panel (or an honest
    /// "estimate unavailable" note — the estimate never blocks submission).
    /// </summary>
    [ObservableProperty]
    private string _cloudCostText = string.Empty;

    partial void OnIsAwaitingCloudConfirmationChanged(bool value)
    {
        RecalculateSMatrixCommand.NotifyCanExecuteChanged();
        LoadFromFileCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Estimates the cloud cost of <paramref name="request"/> and switches the
    /// dialog into the awaiting-confirmation state. Nothing is solved yet.
    /// </summary>
    private async Task PrepareCloudConfirmationAsync(
        IFdtdCostEstimator estimator, FdtdSMatrixRequest request, CancellationToken ct)
    {
        SolverStatus = string.Format(
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
        SolverStatus = string.Format(
            LocalizationService.Instance.Translate("CompSettings.CloudConfirmPrompt"), SolverLabel);
        IsAwaitingCloudConfirmation = true;
    }

    /// <summary>
    /// Submits the pending cloud job after the user confirmed the estimated cost.
    /// </summary>
    [RelayCommand]
    private async Task ConfirmCloudSubmit()
    {
        var service = _pendingCloudService;
        var request = _pendingCloudRequest;
        var cloudLabel = _pendingCloudLabel;
        ClearPendingCloudJob();
        if (service == null || request == null || _storedSMatrices == null || _liveComponent == null)
            return;

        IsComputing = true;
        _recalcCts = new CancellationTokenSource();
        try
        {
            await ExecuteSolveAsync(service, request, _recalcCts.Token, cloudLabel);
        }
        catch (OperationCanceledException)
        {
            // A cancelled CLOUD wait can leave a submitted job running and billing —
            // say so instead of claiming nothing happened.
            SolverStatus = LocalizationService.Instance.Translate(
                service is IFdtdCostEstimator
                    ? "CompSettings.RecomputeCancelledCloud"
                    : "CompSettings.RecomputeCancelled");
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
    /// Dismisses the cloud confirmation without submitting — no credits are used.
    /// </summary>
    [RelayCommand]
    private void CancelCloudSubmit()
    {
        ClearPendingCloudJob();
        SolverStatus = LocalizationService.Instance.Translate("CompSettings.CloudSubmitCancelled");
    }

    private void ClearPendingCloudJob()
    {
        _pendingCloudRequest = null;
        _pendingCloudService = null;
        _pendingCloudLabel = null;
        IsAwaitingCloudConfirmation = false;
    }
}
