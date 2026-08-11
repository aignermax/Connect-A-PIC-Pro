using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CAP.Avalonia.Controls;
using CAP.Avalonia.Controls.Canvas.BendHandles;
using CAP.Avalonia.Controls.Canvas.SegmentShiftHandles;
using CAP.Avalonia.Controls.Rendering;
using CAP.Avalonia.ViewModels;
using CAP_Core.Components.Connections;

namespace UnitTests.UI;

/// <summary>
/// Scene control for the issue #854 walkthrough: draws a routed METAL (electrical)
/// connection's centerline in a copper tone and then invokes the production
/// <see cref="BendHandleRenderer"/> and <see cref="SegmentShiftHandleRenderer"/> in the
/// world transform — the same calls the DesignCanvas makes, so the now-unlocked
/// electrical edit handles are the shipped pixels (small code-built replica like
/// <see cref="Issue791SegmentShiftSceneControl"/>; the real DesignCanvas needs the
/// full App DI stack and cannot be shown headless).
/// </summary>
internal sealed class Issue854MetalRoutingSceneControl : Control
{
    private const double RouteStrokePx = 4.0;

    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e));
    private static readonly IBrush MetalRouteBrush = new SolidColorBrush(Color.FromRgb(0xd9, 0x8a, 0x3d));

    private readonly MainViewModel _vm;
    private readonly CanvasInteractionState _state;
    private readonly WaveguideConnection _connection;
    private readonly Rect _world;

    public Issue854MetalRoutingSceneControl(MainViewModel vm, CanvasInteractionState state,
                                            WaveguideConnection connection, Rect world)
    {
        _vm = vm;
        _state = state;
        _connection = connection;
        _world = world;
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        double scale = Bounds.Width / _world.Width;
        context.FillRectangle(BackgroundBrush, new Rect(Bounds.Size));
        using var _ = context.PushTransform(
            Matrix.CreateTranslation(-_world.X, -_world.Y) * Matrix.CreateScale(scale, scale));

        DrawRoute(context, scale);
        var renderContext = new CanvasRenderContext
        {
            ViewModel = _vm.Canvas,
            MainViewModel = _vm,
            InteractionState = _state,
            Zoom = scale,
            Bounds = new Rect(Bounds.Size),
        };
        new BendHandleRenderer().Render(context, renderContext);
        new SegmentShiftHandleRenderer().Render(context, renderContext);
    }

    private void DrawRoute(DrawingContext context, double scale)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            var points = Issue705CrossingSceneRenderer.SamplePath(_connection.RoutedPath!);
            g.BeginFigure(new Point(points[0].X, points[0].Y), isFilled: false);
            foreach (var (x, y) in points.Skip(1))
                g.LineTo(new Point(x, y));
            g.EndFigure(isClosed: false);
        }
        context.DrawGeometry(null, new Pen(MetalRouteBrush, RouteStrokePx / scale), geometry);
    }
}
