using OxyPlot;

namespace CAP.Avalonia.Controls.Plotting;

/// <summary>
/// Creates OxyPlot controllers for charts hosted inside a ScrollViewer (issue #693).
/// A plain mouse wheel is deliberately left unbound so the event bubbles up and the
/// surrounding panel scrolls normally; zooming requires Ctrl + wheel (or Cmd + wheel
/// on macOS, where Avalonia reports the Command key as the Meta modifier).
/// All other default interactions (pan, tracker, zoom rectangle) stay unchanged.
/// </summary>
public static class ScrollFriendlyPlotController
{
    /// <summary>
    /// Builds a <see cref="PlotController"/> whose wheel bindings are:
    /// plain wheel → unbound (panel scrolls), Ctrl/Cmd + wheel → <see cref="PlotCommands.ZoomWheel"/>.
    /// </summary>
    /// <returns>A new controller instance (one per PlotView; controllers hold manipulator state).</returns>
    public static PlotController Create()
    {
        var controller = new PlotController();

        // Remove the default plain-wheel zoom so OxyPlot reports the event as
        // unhandled and the surrounding ScrollViewer receives it.
        controller.UnbindMouseWheel();

        // Ctrl + wheel zooms (replaces the default Ctrl+wheel "fine zoom").
        controller.BindMouseWheel(OxyModifierKeys.Control, PlotCommands.ZoomWheel);

        // macOS: the natural modifier is Cmd, which OxyPlot.Avalonia maps to
        // OxyModifierKeys.Windows (Avalonia KeyModifiers.Meta). Accept it too.
        controller.BindMouseWheel(OxyModifierKeys.Windows, PlotCommands.ZoomWheel);

        return controller;
    }
}
