using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Canvas;

namespace CAP.Avalonia.ViewModels.Settings;

/// <summary>
/// Settings page for grid-snapping and pin-alignment guide preferences.
/// Wraps the canvas-owned <see cref="GridSnapSettings"/> and
/// <see cref="AlignmentGuideViewModel"/> so changes are live.
/// </summary>
public class GridSnapSettingsPage : LocalizedSettingsPage
{
    /// <inheritdoc/>
    public override string Icon => "⊞";

    /// <inheritdoc/>
    public override string? Category => "Canvas";

    /// <inheritdoc/>
    public override object ViewModel { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="GridSnapSettingsPage"/>.
    /// </summary>
    public GridSnapSettingsPage(DesignCanvasViewModel canvas, LocalizationService localization)
        : base("Settings.Section.GridAlignment", localization)
    {
        ViewModel = new GridSnapSettingsViewModel(canvas.GridSnap, canvas.AlignmentGuide);
    }
}
