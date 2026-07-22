using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Canvas;

namespace CAP.Avalonia.ViewModels.Settings;

/// <summary>
/// Settings page for the PIC chip footprint — preset selection, custom width/height
/// in millimeters, and live tile-grid count. Apply resizes the canvas boundary and
/// triggers a repaint. Lives in the Settings window because chip dimensions are a
/// design-wide configuration, not a per-action knob.
/// </summary>
public class ChipSizeSettingsPage : LocalizedSettingsPage
{
    /// <inheritdoc/>
    public override string Icon => "📏";

    /// <inheritdoc/>
    public override string? Category => "Canvas";

    /// <inheritdoc/>
    public override object ViewModel { get; }

    /// <summary>Initializes a new instance of <see cref="ChipSizeSettingsPage"/>.</summary>
    public ChipSizeSettingsPage(ChipSizeViewModel chipSizeViewModel, LocalizationService localization)
        : base("Settings.Section.ChipSize", localization)
    {
        ViewModel = chipSizeViewModel;
    }
}
