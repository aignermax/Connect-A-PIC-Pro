using System.ComponentModel;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Localization;
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

    // Monotonic probe counter + the probed backend: a slow probe (Meep's Docker
    // inspect) finishing after a backend switch must not overwrite the new
    // backend's hint/flag with a stale result.
    private int _availabilityProbeSeq;

    // The last applied probe verdict — gates the "Get an API key" link on the
    // unavailability actually being a missing key, not any Tidy3D problem.
    private FdtdAvailability? _lastAvailability;

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
        // Re-raise the localized computed labels (solver label, item name/description)
        // on a live language switch — {loc:Localize} bindings update themselves, these don't.
        LocalizationService.Instance.PropertyChanged += OnLocalizationChanged;
    }

    public IFdtdSMatrixService CurrentService => _registry.GetService(SelectedBackend);

    public bool CurrentBackendCostsCredits => CurrentService is IFdtdCostEstimator;

    public string CurrentSolverLabel => FdtdBackendRegistry.SolverLabel(SelectedBackend);

    public bool HasAvailabilityHint => !string.IsNullOrWhiteSpace(AvailabilityHint);

    // Only a probe that actually reported "no API key" warrants the get-a-key link —
    // any other Tidy3D unavailability (package missing, server down) needs its own fix.
    public bool ShowMissingKeyLink =>
        HasAvailabilityHint && CurrentBackendCostsCredits
        && _lastAvailability?.Reason == FdtdUnavailableReason.MissingApiKey;

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
        _lastAvailability = null;
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

    /// <summary>Unsubscribes from the singleton registry and localization. Hosts call this on window close.</summary>
    public void Dispose()
    {
        _registry.SelectedBackendChanged -= OnRegistrySelectedBackendChanged;
        LocalizationService.Instance.PropertyChanged -= OnLocalizationChanged;
    }

    // {loc:Localize} bindings refresh themselves on a language switch; the labels this
    // VM computes from LocalizationService at access time must be re-raised explicitly.
    // Host VMs listen for CurrentSolverLabel to refresh their compute-button captions.
    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(CurrentSolverLabel));
        foreach (var item in BackendItems)
            item.RefreshLocalizedLabels();
    }

    public async Task<FdtdAvailability> CheckAvailabilityAsync(CancellationToken ct = default)
    {
        // Capture what is being probed BEFORE awaiting: the result may only be applied
        // while this probe is still the newest one for the current selection — a slow
        // probe finishing after a backend switch must not overwrite the new backend's
        // hint/flag.
        var probedBackend = SelectedBackend;
        var seq = ++_availabilityProbeSeq;
        FdtdAvailability availability;
        try
        {
            availability = await _registry.GetService(probedBackend).CheckAvailabilityAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw; // a cancelled probe is no availability verdict — the caller decides
        }
        catch (Exception ex)
        {
            // A crashed probe (subprocess died, Docker socket gone) must not fault the
            // fire-and-forget selection-change task unobserved — surface it as unavailable.
            availability = FdtdAvailability.Unavailable(ex.Message);
        }

        if (seq == _availabilityProbeSeq && probedBackend == SelectedBackend)
            ApplyAvailability(availability);
        return availability;
    }

    private void ApplyAvailability(FdtdAvailability availability)
    {
        _lastAvailability = availability;
        AvailabilityHint = availability.IsAvailable ? string.Empty : availability.Message;
        IsCurrentBackendUnavailable = !availability.IsAvailable;
    }

    public void ClearAvailabilityState()
    {
        _lastAvailability = null;
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

    /// <summary>Re-raises the localized labels after a live language switch.</summary>
    public void RefreshLocalizedLabels()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Description));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionMark))]
    private bool _isSelected;

    public string SelectionMark => IsSelected ? "✓" : string.Empty;

    public IRelayCommand SelectCommand { get; }
}
