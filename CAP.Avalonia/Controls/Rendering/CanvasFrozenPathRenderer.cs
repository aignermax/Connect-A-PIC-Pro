using Avalonia.Media;

namespace CAP.Avalonia.Controls.Rendering;

/// <summary>
/// Renders the pin-less frozen waveguide paths that live directly on the canvas
/// (<see cref="ViewModels.Canvas.DesignCanvasViewModel.CanvasFrozenPaths"/>, issue #856).
/// Reuses the group frozen-path drawing (layer-tagged palette pens, per-segment
/// culling); a selected path draws with the same yellow highlight as a selected
/// waveguide connection. Implements <see cref="ICanvasRenderer"/> for world-space rendering.
/// </summary>
public sealed class CanvasFrozenPathRenderer : ICanvasRenderer
{
    // Static readonly — matches the selected-connection pen in WaveguideConnectionRenderer.
    private static readonly Pen SelectedPen = new(Brushes.Yellow, 3);

    /// <inheritdoc/>
    public void Render(DrawingContext context, CanvasRenderContext rc)
    {
        if (rc.ViewModel.CanvasFrozenPaths.Count == 0)
            return;

        var viewport = RenderCulling.ComputeViewportWorld(rc.ViewModel.PanX, rc.ViewModel.PanY, rc.Bounds, rc.Zoom);
        var cullRect = RenderCulling.InflateForCulling(viewport, rc.Zoom);

        foreach (var pathVm in rc.ViewModel.CanvasFrozenPaths)
        {
            if (RenderCulling.GetFrozenPathBounds(pathVm.Path) is { } bounds && !cullRect.Intersects(bounds))
                continue;

            // Canvas-level paths never carry simulated power (pin-less ⇒ not simulated),
            // so no power-flow result is passed.
            ComponentGroupRenderer.RenderFrozenWaveguidePath(
                context, pathVm.Path, powerFlowResult: null, cullRect: cullRect,
                penOverride: pathVm.IsSelected ? SelectedPen : null);
        }
    }
}
