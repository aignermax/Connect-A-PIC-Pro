using CommunityToolkit.Mvvm.ComponentModel;
using CAP_Core.Components.ComponentHelpers;

namespace CAP.Avalonia.ViewModels.Simulation;

/// <summary>
/// Configuration for a laser light source (wavelength and input power).
/// Applied per light source component (Grating Coupler, Edge Coupler).
/// </summary>
public partial class LaserConfig : ObservableObject
{
    /// <summary>
    /// Whether this coupler's laser emits light (Issue #690). A coupler with its
    /// laser ON is an input; with the laser OFF it is listen-only, i.e. an output.
    /// Defaults to true so existing designs keep today's behaviour.
    /// </summary>
    [ObservableProperty]
    private bool _isEnabled = true;

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
