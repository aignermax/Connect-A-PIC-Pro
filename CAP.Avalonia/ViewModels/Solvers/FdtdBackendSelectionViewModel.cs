using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Solvers;
using CAP_Core.Solvers.Fdtd;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Solvers;

// FDTD backend picker: exposes the registered backends, persists the choice through
// FdtdBackendRegistry, and surfaces an actionable hint when the selected backend
// can't run (Docker down, tidy3d missing, no API key). Enum-bound — no display-name
// round-trip. Shared by every flow that recomputes S-matrices.
// Implements IDisposable: every instance subscribes to the singleton registry's
// SelectedBackendChanged (cross-window sync), so hosts must Dispose it on window
// close or the registry keeps probing availability for dead windows.
public partial class FdtdBackendSelectionViewModel : ObservableObject, IDisposable
{
    public const string Tidy3dPortalUrl = "https://tidy3d.simulation.cloud";

    private readonly FdtdBackendRegistry _registry;
    private readonly IUrlLauncher? _urlLauncher;

    public IReadOnlyList<FdtdBackendType> AvailableBackends { get; }

    public IReadOnlyList<FdtdBackendItemViewModel> BackendItems { get; }

    [ObservableProperty]
    private FdtdBackendType _selectedBackend;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAvailabilityHint))]
    [NotifyPropertyChangedFor(nameof(ShowMissingKeyLink))]
    [NotifyPropertyChangedFor(nameof(ShowOpenSettingsLink))]
    private string _availabilityHint = string.Empty;

    [ObservableProperty]
    private bool _isCurrentBackendUnavailable;

    private Action? _openTidy3dSettingsPage;

    // Route to Lunima's own Settings window (Tidy3D Cloud page), injected by the
    // host window — the selection VM stays free of view dependencies.
    public Action? OpenTidy3dSettingsPage
    {
        get => _openTidy3dSettingsPage;
        set
        {
            _openTidy3dSettingsPage = value;
            OnPropertyChanged(nameof(ShowOpenSettingsLink));
        }
    }

    public FdtdBackendSelectionViewModel(FdtdBackendRegistry registry, IUrlLauncher? urlLauncher = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _urlLauncher = urlLauncher;
        AvailableBackends = registry.AvailableBackends;
        BackendItems = AvailableBackends.Select(b => new FdtdBackendItemViewModel(b, this)).ToList();
        _selectedBackend = registry.SelectedBackend;
        SyncItemSelection();
        // Follow backend changes made in another window's picker (shared singleton registry).
        _registry.SelectedBackendChanged += OnRegistrySelectedBackendChanged;
    }

    public IFdtdSMatrixService CurrentService => _registry.GetService(SelectedBackend);

    public bool CurrentBackendCostsCredits => CurrentService is IFdtdCostEstimator;

    public string CurrentSolverLabel => FdtdBackendRegistry.SolverLabel(SelectedBackend);

    public bool HasAvailabilityHint => !string.IsNullOrWhiteSpace(AvailabilityHint);

    public bool ShowMissingKeyLink => HasAvailabilityHint && CurrentBackendCostsCredits;

    public bool ShowOpenSettingsLink => ShowMissingKeyLink && OpenTidy3dSettingsPage != null;

    partial void OnSelectedBackendChanged(FdtdBackendType value)
    {
        // Guard against echo: when this change came FROM the registry (another window's
        // picker via OnRegistrySelectedBackendChanged), the value is already persisted —
        // writing it again would fire SelectedBackendChanged a second time.
        if (_registry.SelectedBackend != value)
            _registry.SelectedBackend = value;
        AvailabilityHint = string.Empty;
        IsCurrentBackendUnavailable = false;
        SyncItemSelection();
        OnPropertyChanged(nameof(CurrentService));
        OnPropertyChanged(nameof(CurrentBackendCostsCredits));
        OnPropertyChanged(nameof(CurrentSolverLabel));
        // Probe right away so a known-bad pick (e.g. Tidy3D without API key) warns immediately,
        // not only when the user clicks compute.
        _ = CheckAvailabilityAsync();
    }

    private void SyncItemSelection()
    {
        foreach (var item in BackendItems)
            item.IsSelected = item.Backend == SelectedBackend;
    }

    // The registry is a DI singleton shared by all open pickers (component settings,
    // NewComponent editor): when the backend is switched elsewhere, mirror it here.
    private void OnRegistrySelectedBackendChanged(object? sender, EventArgs e)
    {
        if (SelectedBackend != _registry.SelectedBackend)
            SelectedBackend = _registry.SelectedBackend;
    }

    /// <summary>Unsubscribes from the singleton registry. Hosts call this on window close.</summary>
    public void Dispose() => _registry.SelectedBackendChanged -= OnRegistrySelectedBackendChanged;

    public async Task<FdtdAvailability> CheckAvailabilityAsync(CancellationToken ct = default)
    {
        var availability = await CurrentService.CheckAvailabilityAsync(ct);
        AvailabilityHint = availability.IsAvailable ? string.Empty : availability.Message;
        IsCurrentBackendUnavailable = !availability.IsAvailable;
        return availability;
    }

    public void ClearAvailabilityState()
    {
        AvailabilityHint = string.Empty;
        IsCurrentBackendUnavailable = false;
    }

    [RelayCommand]
    private void OpenTidy3dPortal() => _urlLauncher?.Open(Tidy3dPortalUrl);

    [RelayCommand]
    private void OpenTidy3dSettings() => OpenTidy3dSettingsPage?.Invoke();
}

public partial class FdtdBackendItemViewModel : ObservableObject
{
    public FdtdBackendItemViewModel(FdtdBackendType backend, FdtdBackendSelectionViewModel parent)
    {
        Backend = backend;
        SelectCommand = new RelayCommand(() => parent.SelectedBackend = backend);
    }

    public FdtdBackendType Backend { get; }

    public string Icon => Backend == FdtdBackendType.Tidy3D ? "☁️" : "🐳";

    public string Name => FdtdBackendRegistry.DisplayName(Backend);

    public string Description => FdtdBackendRegistry.Description(Backend);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionMark))]
    private bool _isSelected;

    public string SelectionMark => IsSelected ? "✓" : string.Empty;

    public IRelayCommand SelectCommand { get; }
}
