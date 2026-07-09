using CommunityToolkit.Mvvm.ComponentModel;
using CAP_Core.Components.ComponentHelpers;

namespace CAP.Avalonia.ViewModels.Simulation;

/// <summary>
/// Configuration for a laser light source (wavelength and input power).
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

    /// <summary>
    /// Display label for the selected wavelength.
    /// </summary>
    public string WavelengthLabel => WavelengthOption.GetLabel(WavelengthNm);

    partial void OnWavelengthNmChanged(int value)
    {
        OnPropertyChanged(nameof(WavelengthLabel));
    }
}
