using CAP.Avalonia.ViewModels.Canvas;

namespace CAP.Avalonia.ViewModels.Settings;

/// <summary>
/// Settings page for waveguide routing preferences (e.g. diagonal routing).
/// Wraps the canvas-owned routing flags so changes take effect immediately.
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
    public RoutingSettingsPage(DesignCanvasViewModel canvas)
    {
        ViewModel = new RoutingSettingsViewModel(canvas);
    }
}
