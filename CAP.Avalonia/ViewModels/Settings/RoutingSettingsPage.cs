using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Canvas.CrossingInsertion;

namespace CAP.Avalonia.ViewModels.Settings;

/// <summary>
/// Settings page for waveguide routing preferences: diagonal routing and
/// adaptive crossing insertion. Wraps live canvas objects so changes take
/// effect immediately.
/// </summary>
public class RoutingSettingsPage : ISettingsPage
{
    /// <inheritdoc/>
    public string Title => "Routing";

    /// <inheritdoc/>
    public string Icon => "⤢";

    /// <inheritdoc/>
    public string? Category => "Canvas";

    /// <inheritdoc/>
    public object ViewModel { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="RoutingSettingsPage"/>.
    /// </summary>
    /// <param name="canvas">The live canvas ViewModel.</param>
    /// <param name="crossingBinder">Injected crossing-insertion binder (DI
    /// singleton). Null in tests / headless contexts that bypass DI.</param>
    public RoutingSettingsPage(DesignCanvasViewModel canvas,
                               CrossingInsertionCanvasBinder? crossingBinder = null)
    {
        ViewModel = new RoutingSettingsViewModel(canvas, crossingBinder);
    }
}
