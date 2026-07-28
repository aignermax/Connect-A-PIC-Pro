using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CAP.Avalonia.Controls;
using CAP.Avalonia.Controls.Rendering;
using CAP.Avalonia.ViewModels.Canvas;

namespace UnitTests.UI;

/// <summary>
/// Scene control for the canvas label-declutter walkthrough: renders a
/// <see cref="DesignCanvasViewModel"/> using the production <see cref="WaveguideConnectionRenderer"/>
/// and <see cref="ComponentRenderer"/> in the world transform — the same two renderers
/// <c>DesignCanvas.Render</c> calls in the same order, so the captured pixels (hover-only length
/// labels, overlap-decluttered names, screen-space font cap) are exactly what ships (small
/// code-built replica like <see cref="Issue798CutToolSceneControl"/>; the real DesignCanvas needs
/// the full App DI stack and cannot be shown headless).
/// </summary>
internal sealed class CanvasLabelDeclutterSceneControl : Control
{
    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e));

    private readonly DesignCanvasViewModel _canvas;
    private readonly CanvasInteractionState _state;
    private readonly Rect _world;
    private readonly WaveguideConnectionRenderer _connectionRenderer = new();
    private readonly ComponentRenderer _componentRenderer = new();

    /// <param name="world">
    /// World (µm) region to display, mapped to fill <see cref="Visual.Bounds"/> exactly — a
    /// smaller region at the same pixel size is how a "zoomed in" step is produced, matching
    /// how <see cref="Issue798CutToolSceneControl"/> derives its scale.
    /// </param>
    public CanvasLabelDeclutterSceneControl(DesignCanvasViewModel canvas, CanvasInteractionState state, Rect world)
    {
        _canvas = canvas;
        _state = state;
        _world = world;
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        double scale = Bounds.Width / _world.Width;
        // ComponentRenderer recovers the viewport from Pan/Zoom (-PanX/zoom, ...) exactly like
        // the real canvas — Pan must be scaled for that formula to recover _world.X/.Y.
        _canvas.PanX = -_world.X * scale;
        _canvas.PanY = -_world.Y * scale;

        context.FillRectangle(BackgroundBrush, new Rect(Bounds.Size));

        var rc = new CanvasRenderContext
        {
            ViewModel = _canvas,
            InteractionState = _state,
            Zoom = scale,
            Bounds = new Rect(Bounds.Size),
        };

        // The actual pixel transform maps _world.X/.Y to screen (0,0) directly (translate raw
        // world units, then scale) — NOT via the already-scaled Pan above, which would double
        // apply the scale factor to the translation.
        using var _ = context.PushTransform(
            Matrix.CreateTranslation(-_world.X, -_world.Y) * Matrix.CreateScale(scale, scale));

        // Same draw order as DesignCanvas.Render: waveguides under components, then the
        // deferred label flush on top of all geometry (without it no name/length label
        // would be captured at all — the renderers now enqueue their text instead of
        // drawing it inline).
        _connectionRenderer.Render(context, rc);
        _componentRenderer.Render(context, rc);
        rc.Labels.Flush(context, scale);
    }
}
