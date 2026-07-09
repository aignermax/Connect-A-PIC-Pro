using CAP_Core.Components.Process;
using CAP_Core.Solvers.ModeProbe;
using CAP_Core.Solvers.ModeSolver;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Solvers.ModeProbe;

/// <summary>
/// ViewModel for the mode-slice probe: a non-modal flyout opened by clicking a
/// waveguide (or coupler) on the canvas. Auto-fills the cross-section from the
/// clicked connection and the active PDK process, solves the fundamental mode via
/// the existing <see cref="IModeSolverService"/> backends (femwell/EMpy/Tidy3D),
/// and — for grating/edge couplers — reports the Gaussian fiber-overlap efficiency.
/// State/commands live here; the solve pipeline is in ModeProbeViewModel.Solve.cs.
/// </summary>
public partial class ModeProbeViewModel : ObservableObject
{
    // ── panel state ─────────────────────────────────────────────────────────

    /// <summary>True while the probe flyout is visible.</summary>
    [ObservableProperty] private bool _isOpen;

    /// <summary>Panel left position in canvas-control pixels (set at the click point).</summary>
    [ObservableProperty] private double _panelX;

    /// <summary>Panel top position in canvas-control pixels (set at the click point).</summary>
    [ObservableProperty] private double _panelY;

    /// <summary>Header label describing the probed element.</summary>
    [ObservableProperty] private string _targetName = "";

    // ── inputs (editable inline) ────────────────────────────────────────────

    /// <summary>Probe wavelength in nm; defaults to the current simulation wavelength.</summary>
    [ObservableProperty] private double _wavelengthNm = DefaultWavelengthNm;

    /// <summary>Selected mode-solver backend name (femwell default, Tidy3D when configured).</summary>
    [ObservableProperty] private string _selectedBackend = nameof(ModeSolverBackend.GdsfactoryModes);

    /// <summary>Gaussian fiber mode-field diameter in µm (default SMF-28 @1550 nm).</summary>
    [ObservableProperty] private double _fiberMfdUm = FiberOverlapCalculator.DefaultFiberMfdMicrometers;

    /// <summary>Backend names for the ComboBox.</summary>
    public static IReadOnlyList<string> AvailableBackends { get; } = Enum.GetNames<ModeSolverBackend>();

    // ── results / notices ───────────────────────────────────────────────────

    /// <summary>True while a solve runs.</summary>
    [ObservableProperty] private bool _isSolving;

    /// <summary>Status / error text shown at the bottom of the flyout.</summary>
    [ObservableProperty] private string _statusText = "";

    /// <summary>True once a mode result is available for display.</summary>
    [ObservableProperty] private bool _hasResult;

    /// <summary>Effective index of the fundamental mode.</summary>
    [ObservableProperty] private double _nEff;

    /// <summary>Group index of the fundamental mode.</summary>
    [ObservableProperty] private double _nGroup;

    /// <summary>Dominant polarisation ("TE", "TM", "hybrid").</summary>
    [ObservableProperty] private string _polarisation = "";

    /// <summary>Mode-field diameter summary, e.g. "1.1 × 0.9 µm".</summary>
    [ObservableProperty] private string _mfdText = "";

    /// <summary>Decoded mode-field intensity image, when the backend produced one.</summary>
    [ObservableProperty] private global::Avalonia.Media.Imaging.Bitmap? _modeFieldImage;

    /// <summary>True when any cross-section value is a fallback (shows the "check values" hint).</summary>
    [ObservableProperty] private bool _isGeometryAssumed;

    /// <summary>Cross-section summary line, e.g. "0.45 × 0.22 µm · n 3.48/1.44".</summary>
    [ObservableProperty] private string _crossSectionText = "";

    /// <summary>Provenance of the cross-section values.</summary>
    [ObservableProperty] private string _geometrySourceText = "";

    /// <summary>True when the probe landed in an MMI/interference region — no mode is shown.</summary>
    [ObservableProperty] private bool _isInterferenceRegion;

    /// <summary>True for grating/edge couplers — shows the fiber-overlap section.</summary>
    [ObservableProperty] private bool _showFiberOverlap;

    /// <summary>Fiber coupling efficiency in percent.</summary>
    [ObservableProperty] private double _overlapPercent;

    /// <summary>Fiber coupling loss in dB.</summary>
    [ObservableProperty] private double _overlapLossDb;

    // ── dependencies / wiring ───────────────────────────────────────────────

    private const double DefaultWavelengthNm = 1550;
    private readonly IModeSolverService _service;
    private readonly CrossSectionDefaultsStore _defaults;
    private CancellationTokenSource? _cts;
    private ProbeTarget? _target;
    private (double MfdX, double MfdY)? _modeMfd;

    /// <summary>Returns the active PDK process fingerprint; wired by MainViewModel.</summary>
    public Func<ProcessFingerprint?>? GetActiveProcessFingerprint { get; set; }

    /// <summary>Returns the current simulation wavelength in nm; wired by MainViewModel.</summary>
    public Func<double?>? GetSimulationWavelengthNm { get; set; }

    /// <summary>Initialises the probe ViewModel.</summary>
    public ModeProbeViewModel(IModeSolverService service, CrossSectionDefaultsStore defaults)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _defaults = defaults ?? throw new ArgumentNullException(nameof(defaults));
    }

    // ── public API ──────────────────────────────────────────────────────────

    /// <summary>
    /// Opens the probe flyout at the given canvas-control position for the given
    /// target and starts a solve (unless the target is an interference region).
    /// </summary>
    public void Open(ProbeTarget target, double panelX, double panelY)
    {
        _target = target;
        PanelX = panelX;
        PanelY = panelY;
        TargetName = target.DisplayName;
        IsInterferenceRegion = target.IsInterferenceRegion;
        ShowFiberOverlap = target.IsFiberCoupler;
        WavelengthNm = GetSimulationWavelengthNm?.Invoke() ?? DefaultWavelengthNm;
        ResetResult();
        IsOpen = true;

        if (IsInterferenceRegion)
        {
            StatusText = "Interference region — a single mode slice is not meaningful here. Use FDTD.";
            return;
        }
        SolveCommand.Execute(null);
    }

    /// <summary>Closes the flyout and cancels any running solve.</summary>
    [RelayCommand]
    private void Close()
    {
        _cts?.Cancel();
        IsOpen = false;
    }

    /// <summary>Recomputes the fiber overlap when the fiber MFD is edited inline.</summary>
    partial void OnFiberMfdUmChanged(double value) => UpdateFiberOverlap();

    private void ResetResult()
    {
        HasResult = false;
        StatusText = "";
        ModeFieldImage = null;
        MfdText = "";
        _modeMfd = null;
        OverlapPercent = 0;
        OverlapLossDb = 0;
    }
}
