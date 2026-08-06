using CommunityToolkit.Mvvm.ComponentModel;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.ExternalPorts.LaserSpectrum;

namespace CAP.Avalonia.ViewModels.Simulation;

/// <summary>
/// Configuration for a laser light source (wavelength, input power and — Issue
/// #819 — the spectral model: line shape, linewidth and relative intensity noise).
/// Applied per light source component (Grating Coupler, Edge Coupler).
/// </summary>
public partial class LaserConfig : ObservableObject
{
    private readonly CAP_Core.Components.Core.Component? _component;
    private bool _isEnabledLocal = true;

    /// <summary>Creates a config backed by the given component's core laser flag.</summary>
    /// <param name="component">The owning component; its <c>LaserEnabled</c> flag backs
    /// <see cref="IsEnabled"/> so the state survives ViewModel recreation (grouping,
    /// ungrouping, delete/undo). Null (tests) falls back to a local field.</param>
    public LaserConfig(CAP_Core.Components.Core.Component? component = null)
    {
        _component = component;
    }

    /// <summary>
    /// Whether this coupler's laser emits light (Issue #690). A coupler with its
    /// laser ON is an input; with the laser OFF it is listen-only, i.e. an output.
    /// Defaults to true so existing designs keep today's behaviour. Backed by
    /// <c>Component.LaserEnabled</c> so grouping and undo cannot lose the role.
    /// </summary>
    public bool IsEnabled
    {
        get => _component?.LaserEnabled ?? _isEnabledLocal;
        set
        {
            if (IsEnabled == value)
                return;
            if (_component != null)
                _component.LaserEnabled = value;
            else
                _isEnabledLocal = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Selected wavelength in nanometers.
    /// </summary>
    [ObservableProperty]
    private int _wavelengthNm = StandardWaveLengths.RedNM;

    /// <summary>
    /// Optical input power (linear, 0.0 to 1.0).
    /// </summary>
    [ObservableProperty]
    private double _inputPower = 1.0;

    /// <summary>Default linewidth offered when the user switches away from the ideal shape.</summary>
    public const double DefaultLinewidthFwhmNm = 2.0;

    /// <summary>
    /// Spectral line shape of the source. <see cref="LaserLineShape.Ideal"/> (the
    /// default) reproduces today's monochromatic behaviour exactly.
    /// </summary>
    [ObservableProperty]
    private LaserLineShape _lineShape = LaserLineShape.Ideal;

    /// <summary>Linewidth (FWHM) in nm; only applied when <see cref="LineShape"/> is not ideal.</summary>
    [ObservableProperty]
    private double _linewidthFwhmNm = DefaultLinewidthFwhmNm;

    /// <summary>Relative intensity noise in dB/Hz, fed into the eye-diagram receiver noise model.</summary>
    [ObservableProperty]
    private double _rinDbPerHz = LaserSpectrumModel.DefaultRinDbPerHz;

    /// <summary>True when the source has a finite linewidth (non-ideal shape).</summary>
    public bool IsSpectralShape => LineShape != LaserLineShape.Ideal;

    /// <summary>
    /// Display label for the selected wavelength.
    /// </summary>
    public string WavelengthLabel => WavelengthOption.GetLabel(WavelengthNm);

    /// <summary>Builds the core spectrum model for this configuration.</summary>
    public LaserSpectrumModel ToSpectrum() => new(
        WavelengthNm,
        LineShape,
        IsSpectralShape ? LinewidthFwhmNm : 0,
        RinDbPerHz);

    partial void OnWavelengthNmChanged(int value)
    {
        OnPropertyChanged(nameof(WavelengthLabel));
    }

    partial void OnLineShapeChanged(LaserLineShape value)
    {
        OnPropertyChanged(nameof(IsSpectralShape));
    }
}
