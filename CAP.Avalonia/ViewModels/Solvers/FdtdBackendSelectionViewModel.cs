using CAP.Avalonia.Services.Solvers;
using CAP_Core.Solvers.Fdtd;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.Solvers;

/// <summary>
/// Reusable FDTD backend picker: exposes the registered backends by display name,
/// persists the user's choice through <see cref="FdtdBackendRegistry"/>, and
/// surfaces an actionable prerequisite hint when the selected backend can't run
/// (Docker not running, tidy3d missing, no API key). Shared by the component
/// settings dialog and, later, the new-component flow — deliberately not buried
/// in any one dialog's code.
/// </summary>
public partial class FdtdBackendSelectionViewModel : ObservableObject
{
    private readonly FdtdBackendRegistry _registry;

    /// <summary>Display names of all registered backends, in registry order.</summary>
    public IReadOnlyList<string> AvailableBackendNames { get; }

    /// <summary>
    /// Display name of the selected backend (bound to the picker ComboBox).
    /// Setting it persists the choice and clears the previous availability hint.
    /// </summary>
    [ObservableProperty]
    private string _selectedBackendName;

    /// <summary>
    /// Actionable message shown under the picker when the selected backend's
    /// prerequisites are missing; empty when nothing needs fixing.
    /// </summary>
    [ObservableProperty]
    private string _availabilityHint = string.Empty;

    /// <summary>Initializes the picker from the registry's persisted selection.</summary>
    public FdtdBackendSelectionViewModel(FdtdBackendRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        AvailableBackendNames = registry.AvailableBackends
            .Select(FdtdBackendRegistry.DisplayName)
            .ToList();
        _selectedBackendName = FdtdBackendRegistry.DisplayName(registry.SelectedBackend);
    }

    /// <summary>The backend currently selected in the picker.</summary>
    public FdtdBackendType SelectedBackend =>
        _registry.AvailableBackends.FirstOrDefault(
            b => FdtdBackendRegistry.DisplayName(b) == SelectedBackendName);

    /// <summary>The solver service for the selected backend.</summary>
    public IFdtdSMatrixService CurrentService => _registry.GetService(SelectedBackend);

    /// <summary>
    /// True when the selected backend charges per run (implements
    /// <see cref="IFdtdCostEstimator"/>), so callers must confirm before submitting.
    /// </summary>
    public bool CurrentBackendCostsCredits => CurrentService is IFdtdCostEstimator;

    /// <summary>Short label ("Meep", "Tidy3D") for status texts and override notes.</summary>
    public string CurrentSolverLabel => FdtdBackendRegistry.SolverLabel(SelectedBackend);

    partial void OnSelectedBackendNameChanged(string value)
    {
        var backend = _registry.AvailableBackends.FirstOrDefault(
            b => FdtdBackendRegistry.DisplayName(b) == value);
        _registry.SelectedBackend = backend;
        AvailabilityHint = string.Empty;
        OnPropertyChanged(nameof(SelectedBackend));
        OnPropertyChanged(nameof(CurrentService));
        OnPropertyChanged(nameof(CurrentBackendCostsCredits));
        OnPropertyChanged(nameof(CurrentSolverLabel));
    }

    /// <summary>
    /// Probes the selected backend and stores its message in
    /// <see cref="AvailabilityHint"/> when it can't run, so the picker stays
    /// visible-but-explained instead of silently failing later.
    /// </summary>
    public async Task<FdtdAvailability> CheckAvailabilityAsync(CancellationToken ct = default)
    {
        var availability = await CurrentService.CheckAvailabilityAsync(ct);
        AvailabilityHint = availability.IsAvailable ? string.Empty : availability.Message;
        return availability;
    }
}
