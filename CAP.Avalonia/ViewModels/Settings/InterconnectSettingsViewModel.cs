using System.Globalization;
using CAP.Avalonia.Services;
using CAP_Core.Routing.InterconnectRouting;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Settings;

/// <summary>
/// ViewModel for the Interconnect settings page (issue #574): global defaults for
/// waveguide width, bend radius and optional GDS layer used by the Nazca export
/// header and styled connections. Persisted in user preferences.
/// </summary>
public partial class InterconnectSettingsViewModel : ObservableObject
{
    private readonly UserPreferencesService _preferences;

    /// <summary>Waveguide width in micrometers (Nazca WG_WIDTH).</summary>
    [ObservableProperty]
    private double _widthMicrometers;

    /// <summary>Default bend radius in micrometers (Nazca BEND_RADIUS).</summary>
    [ObservableProperty]
    private double _bendRadiusMicrometers;

    /// <summary>GDS layer as text; empty means the PDK/Nazca default layer.</summary>
    [ObservableProperty]
    private string _gdsLayerText = "";

    /// <summary>Feedback shown after applying or on invalid input.</summary>
    [ObservableProperty]
    private string _statusText = "";

    /// <summary>Initializes the ViewModel from the persisted preferences.</summary>
    public InterconnectSettingsViewModel(UserPreferencesService preferences)
    {
        _preferences = preferences;
        var settings = preferences.GetInterconnectSettings();
        WidthMicrometers = settings.WidthMicrometers;
        BendRadiusMicrometers = settings.BendRadiusMicrometers;
        GdsLayerText = settings.GdsLayer?.ToString(CultureInfo.InvariantCulture) ?? "";
    }

    /// <summary>Validates the inputs and persists the interconnect settings.</summary>
    [RelayCommand]
    private void Apply()
    {
        if (WidthMicrometers <= 0 || BendRadiusMicrometers <= 0)
        {
            StatusText = "Width and bend radius must be positive.";
            return;
        }

        int? layer = null;
        var layerText = GdsLayerText?.Trim() ?? "";
        if (layerText.Length > 0)
        {
            if (!int.TryParse(layerText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                StatusText = "GDS layer must be an integer (or empty for the PDK default).";
                return;
            }
            layer = parsed;
        }

        _preferences.SetInterconnectSettings(new InterconnectSettings
        {
            WidthMicrometers = WidthMicrometers,
            BendRadiusMicrometers = BendRadiusMicrometers,
            GdsLayer = layer,
        });
        StatusText = "Interconnect settings saved.";
    }

    /// <summary>Resets the fields to the built-in export defaults.</summary>
    [RelayCommand]
    private void ResetToDefaults()
    {
        WidthMicrometers = InterconnectSettings.DefaultWidthMicrometers;
        BendRadiusMicrometers = InterconnectSettings.DefaultBendRadiusMicrometers;
        GdsLayerText = "";
        StatusText = "Defaults restored — click Apply to save.";
    }
}
