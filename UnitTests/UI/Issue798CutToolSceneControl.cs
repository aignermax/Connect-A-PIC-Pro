using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CAP.Avalonia.Controls;
using CAP.Avalonia.Controls.Canvas.CutTool;
using CAP.Avalonia.Controls.Rendering;
using CAP.Avalonia.ViewModels;
using CAP_Core.Routing;

namespace UnitTests.UI;

/// <summary>
/// Scene control for the Cut-tool walkthrough: draws the placed components and routed
/// waveguides of the canvas ViewModel, then invokes the production
/// <see cref="CutToolOverlayRenderer"/> in the world transform — the same call the
/// DesignCanvas makes, so guide lines and candidate markers are the shipped pixels
/// (small code-built replica like <see cref="Issue791SegmentShiftSceneControl"/>;
/// the real DesignCanvas needs the full App DI stack and cannot be shown headless).
/// </summary>
internal sealed class Issue798CutToolSceneControl : Control
{
    private const double RouteStrokePx = 3.0;
    private const double ComponentStrokePx = 1.5;

    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e));
    private static readonly IBrush RouteBrush = new SolidColorBrush(Colors.DeepSkyBlue);
    private static readonly IBrush ComponentFill = new SolidColorBrush(Color.FromRgb(0x2f, 0x2f, 0x3a));
    private static readonly IBrush ComponentStroke = new SolidColorBrush(Color.FromRgb(0x8a, 0x8a, 0xa0));
    private static readonly IBrush CrossingFill = new SolidColorBrush(Color.FromRgb(0x4a, 0x3a, 0x18));

    private readonly MainViewModel _vm;
    private readonly CanvasInteractionState _state;
    private readonly CutToolOverlayRenderer _overlay = new();
    private readonly Rect _world;

    public Issue798CutToolSceneControl(MainViewModel vm, CanvasInteractionState state, Rect world)
    {
        _vm = vm;
        _state = state;
        _world = world;
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        double scale = Bounds.Width / _world.Width;
        // The overlay derives its viewport from Pan/Zoom exactly like on the canvas.
        _vm.Canvas.PanX = -_world.X * scale;
        _vm.Canvas.PanY = -_world.Y * scale;

        context.FillRectangle(BackgroundBrush, new Rect(Bounds.Size));
        using var _ = context.PushTransform(
            Matrix.CreateTranslation(-_world.X, -_world.Y) * Matrix.CreateScale(scale, scale));

        DrawComponents(context, scale);
        DrawRoutes(context, scale);
        _overlay.Render(context, new CanvasRenderContext
        {
            ViewModel = _vm.Canvas,
            MainViewModel = _vm,
            InteractionState = _state,
            Zoom = scale,
            Bounds = new Rect(Bounds.Size),
        });
    }

    private void DrawComponents(DrawingContext context, double scale)
    {
        var pen = new Pen(ComponentStroke, ComponentStrokePx / scale);
        foreach (var component in _vm.Canvas.Components)
        {
            bool isCrossing = component.Component.NazcaFunctionName == "ebeam_crossing4";
            var rect = new Rect(component.X, component.Y, component.Width, component.Height);
            context.DrawRectangle(isCrossing ? CrossingFill : ComponentFill, pen, rect);
        }
    }

    private void DrawRoutes(DrawingContext context, double scale)
    {
        var pen = new Pen(RouteBrush, RouteStrokePx / scale);
        foreach (var connectionVm in _vm.Canvas.Connections)
        {
            var connection = connectionVm.Connection;
            var segments = connection.GetPathSegments();
            if (segments.Count == 0)
            {
                // Freshly split halves may not be re-routed yet — draw the direct line.
                var (sx, sy) = connection.StartPin.GetAbsolutePosition();
                var (ex, ey) = connection.EndPin.GetAbsolutePosition();
                context.DrawLine(pen, new Point(sx, sy), new Point(ex, ey));
                continue;
            }
            foreach (var straight in segments.OfType<StraightSegment>())
                context.DrawLine(pen,
                    new Point(straight.StartPoint.X, straight.StartPoint.Y),
                    new Point(straight.EndPoint.X, straight.EndPoint.Y));
        }
    }
}
