using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CAP.Avalonia.Controls;
using CAP.Avalonia.Controls.Canvas.SegmentShiftHandles;
using CAP.Avalonia.Controls.Rendering;
using CAP.Avalonia.ViewModels;
using CAP_Core.Components.Connections;

namespace UnitTests.UI;

/// <summary>
/// Scene control for the issue #791 walkthrough: draws the routed centerline and then
/// invokes the production <see cref="SegmentShiftHandleRenderer"/> in the world transform —
/// the same call the DesignCanvas makes, so handle shape, colors and Δ label are the
/// shipped pixels (small code-built replica like <see cref="Issue705CrossingSceneRenderer"/>;
/// the real DesignCanvas needs the full App DI stack and cannot be shown headless).
/// </summary>
internal sealed class Issue791SegmentShiftSceneControl : Control
{
    private const double RouteStrokePx = 3.0;

    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e));
    private static readonly IBrush RouteBrush = new SolidColorBrush(Colors.DeepSkyBlue);

    private readonly MainViewModel _vm;
    private readonly CanvasInteractionState _state;
    private readonly WaveguideConnection _connection;
    private readonly Rect _world;

    public Issue791SegmentShiftSceneControl(MainViewModel vm, CanvasInteractionState state,
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
        new SegmentShiftHandleRenderer().Render(context, new CanvasRenderContext
        {
            ViewModel = _vm.Canvas,
            MainViewModel = _vm,
            InteractionState = _state,
            Zoom = scale,
            Bounds = new Rect(Bounds.Size),
        });
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
        context.DrawGeometry(null, new Pen(RouteBrush, RouteStrokePx / scale), geometry);
    }
}
