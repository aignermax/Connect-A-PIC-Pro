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

    partial void OnIsAwaitingCloudConfirmationChanged(bool value) =>
        RecalculateSMatrixCommand.NotifyCanExecuteChanged();

    /// <summary>
    /// Estimates the cloud cost of <paramref name="request"/> and switches the
    /// dialog into the awaiting-confirmation state. Nothing is solved yet.
    /// </summary>
    private async Task PrepareCloudConfirmationAsync(
        IFdtdCostEstimator estimator, FdtdSMatrixRequest request, CancellationToken ct)
    {
        SolverStatus = $"Estimating {SolverLabel} cloud cost…";
        var estimate = await estimator.EstimateCostAsync(request, ct);

        CloudCostText = estimate.Success
            ? $"Estimated cost: {estimate.EstimatedCredits:F2} FlexCredits " +
              $"({estimate.SimulationCount} cloud simulation(s), one per port)."
            : $"Cost estimate unavailable: {estimate.Error}";

        _pendingCloudRequest = request;
        _pendingCloudService = (IFdtdSMatrixService)estimator;
        SolverStatus = $"{SolverLabel} runs in the cloud and consumes credits — review the estimate and submit.";
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
        ClearPendingCloudJob();
        if (service == null || request == null || _storedSMatrices == null || _liveComponent == null)
            return;

        IsComputing = true;
        _recalcCts = new CancellationTokenSource();
        try
        {
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
    /// Dismisses the cloud confirmation without submitting — no credits are used.
    /// </summary>
    [RelayCommand]
    private void CancelCloudSubmit()
    {
        ClearPendingCloudJob();
        SolverStatus = "Cloud submission cancelled — nothing was sent, no credits were used.";
    }

    private void ClearPendingCloudJob()
    {
        _pendingCloudRequest = null;
        _pendingCloudService = null;
        IsAwaitingCloudConfirmation = false;
    }
}
