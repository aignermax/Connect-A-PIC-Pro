using CAP.Avalonia.ViewModels.Canvas;

namespace CAP.Avalonia.ViewModels.Settings;

/// <summary>
/// ViewModel for the Routing settings page. Wraps the existing
/// <see cref="DesignCanvasViewModel"/> so toggling a routing option applies
/// immediately (the canvas re-routes all connections on change).
/// </summary>
public class RoutingSettingsViewModel
{
    /// <summary>The canvas whose routing flags this page edits.</summary>
    public DesignCanvasViewModel Canvas { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="RoutingSettingsViewModel"/>.
    /// </summary>
    public RoutingSettingsViewModel(DesignCanvasViewModel canvas)
    {
        Canvas = canvas;
    }
}
