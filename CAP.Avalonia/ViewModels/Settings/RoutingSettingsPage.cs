using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Canvas.CrossingInsertion;

namespace CAP.Avalonia.ViewModels.Settings;

/// <summary>
/// Settings page for waveguide routing preferences: diagonal routing and
/// adaptive crossing insertion. Wraps live canvas objects so changes take
/// effect immediately.
/// </summary>
public class RoutingSettingsPage : LocalizedSettingsPage
{
    /// <inheritdoc/>
    public override string Icon => "⤢";

    /// <inheritdoc/>
    public override string? Category => "Canvas";

    /// <inheritdoc/>
    public override object ViewModel { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="RoutingSettingsPage"/>.
    /// </summary>
    /// <param name="canvas">The live canvas ViewModel.</param>
    /// <param name="localization">The process-wide localization service.</param>
    /// <param name="crossingBinder">Injected crossing-insertion binder (DI
    /// singleton). Null in tests / headless contexts that bypass DI.</param>
    public RoutingSettingsPage(DesignCanvasViewModel canvas,
                               LocalizationService localization,
                               CrossingInsertionCanvasBinder? crossingBinder = null)
        : base("Settings.Section.Routing", localization)
    {
        ViewModel = new RoutingSettingsViewModel(canvas, crossingBinder);
    }
}
