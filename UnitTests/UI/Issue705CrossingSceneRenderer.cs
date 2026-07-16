using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Routing;
using AvaloniaCanvas = Avalonia.Controls.Canvas;

namespace UnitTests.UI;

/// <summary>
/// Draws a crossing-insertion scene (terminal blocks, the auto-inserted crossing
/// component, and routed waveguide centerlines) onto an Avalonia canvas for the
/// issue #705 visual walkthrough. This is a small code-built replica of the design
/// canvas — the real DesignCanvas needs the full App DI stack and cannot be shown
/// headless — but every component and path drawn here is the production state of
/// <c>DesignCanvasViewModel</c> / <c>WaveguideConnectionManager</c>, not synthetic geometry.
/// </summary>
internal static class Issue705CrossingSceneRenderer
{
    /// <summary>Physical waveguide core width used for stroke thickness (µm).</summary>
    private const double WaveguideWidthMicrometers = 1.2;

    /// <summary>Padding added around the auto-fitted world bounds (µm).</summary>
    private const double AutoFitPaddingMicrometers = 25.0;

    /// <summary>Sampling step along routed segments (µm).</summary>
    private const double SampleStepMicrometers = 1.0;

    private static readonly Color[] RouteColors =
    {
        Colors.OrangeRed, Colors.DeepSkyBlue, Colors.LimeGreen, Colors.Gold,
        Colors.Violet, Colors.Turquoise,
    };

    /// <summary>
    /// Renders the given components and routed connections into a canvas of the given
    /// pixel width; the height follows the viewport aspect ratio. Pass
    /// <paramref name="viewport"/> to keep a stable world region across frames, or
    /// null to auto-fit everything. Inserted crossings are highlighted in gold.
    /// </summary>
    public static AvaloniaCanvas Render(
        IReadOnlyList<Component> components,
        IReadOnlyList<WaveguideConnection> connections,
        double canvasWidthPixels,
        Rect? viewport = null)
    {
        var world = viewport ?? AutoFit(components, connections);
        double scale = canvasWidthPixels / world.Width;
        var canvas = new AvaloniaCanvas
        {
            Width = canvasWidthPixels,
            Height = world.Height * scale,
            Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e)),
            ClipToBounds = true,
        };

        foreach (var component in components)
            DrawComponent(canvas, component, world, scale);

        for (int i = 0; i < connections.Count; i++)
            DrawConnection(canvas, connections[i], RouteColors[i % RouteColors.Length], world, scale);

        return canvas;
    }

    /// <summary>Samples a routed path every µm along its straights and bends.</summary>
    public static List<(double X, double Y)> SamplePath(RoutedPath path)
    {
        var points = new List<(double, double)>();
        foreach (var segment in path.Segments)
        {
            int steps = Math.Max(2, (int)Math.Ceiling(segment.LengthMicrometers / SampleStepMicrometers));
            for (int i = 0; i <= steps; i++)
                points.Add(PointOnSegment(segment, (double)i / steps));
        }
        return points;
    }

    /// <summary>World bounds enclosing all component rectangles and routed paths.</summary>
    private static Rect AutoFit(
        IReadOnlyList<Component> components, IReadOnlyList<WaveguideConnection> connections)
    {
        double minX = components.Min(c => c.PhysicalX);
        double minY = components.Min(c => c.PhysicalY);
        double maxX = components.Max(c => c.PhysicalX + c.WidthMicrometers);
        double maxY = components.Max(c => c.PhysicalY + c.HeightMicrometers);
        foreach (var connection in connections)
        {
            if (connection.RoutedPath == null) continue;
            foreach (var (x, y) in SamplePath(connection.RoutedPath))
            {
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }
        return new Rect(
            minX - AutoFitPaddingMicrometers, minY - AutoFitPaddingMicrometers,
            maxX - minX + 2 * AutoFitPaddingMicrometers, maxY - minY + 2 * AutoFitPaddingMicrometers);
    }

    /// <summary>Draws one component as a filled rectangle with its name and pin markers.</summary>
    private static void DrawComponent(AvaloniaCanvas canvas, Component component, Rect world, double scale)
    {
        bool isCrossing = component.IsInsertedCrossing;
        var rect = new Rectangle
        {
            Width = Math.Max(2, component.WidthMicrometers * scale),
            Height = Math.Max(2, component.HeightMicrometers * scale),
            Fill = new SolidColorBrush(isCrossing
                ? Color.FromRgb(0x8a, 0x6d, 0x1a) : Color.FromRgb(0x2d, 0x4f, 0x6d)),
            Stroke = new SolidColorBrush(isCrossing
                ? Colors.Gold : Color.FromRgb(0x6f, 0xa8, 0xdc)),
            StrokeThickness = isCrossing ? 2 : 1,
        };
        AvaloniaCanvas.SetLeft(rect, (component.PhysicalX - world.X) * scale);
        AvaloniaCanvas.SetTop(rect, (component.PhysicalY - world.Y) * scale);
        canvas.Children.Add(rect);

        var label = new TextBlock
        {
            Text = isCrossing ? "crossing" : component.Name,
            Foreground = isCrossing ? Brushes.Gold : Brushes.White,
            FontSize = 12,
        };
        AvaloniaCanvas.SetLeft(label, (component.PhysicalX - world.X) * scale + 3);
        AvaloniaCanvas.SetTop(label, (component.PhysicalY - world.Y) * scale
            + (isCrossing ? component.HeightMicrometers * scale + 2 : 2));
        canvas.Children.Add(label);

        foreach (var pin in component.PhysicalPins)
        {
            var (px, py) = (component.PhysicalX + pin.OffsetXMicrometers,
                            component.PhysicalY + pin.OffsetYMicrometers);
            var dot = new Ellipse { Width = 5, Height = 5, Fill = Brushes.White };
            AvaloniaCanvas.SetLeft(dot, (px - world.X) * scale - 2.5);
            AvaloniaCanvas.SetTop(dot, (py - world.Y) * scale - 2.5);
            canvas.Children.Add(dot);
        }
    }

    /// <summary>Draws one routed connection as a sampled polyline at physical waveguide width.</summary>
    private static void DrawConnection(
        AvaloniaCanvas canvas, WaveguideConnection connection, Color color, Rect world, double scale)
    {
        if (connection.RoutedPath == null || connection.RoutedPath.Segments.Count == 0)
            return;

        var points = new Points();
        foreach (var (x, y) in SamplePath(connection.RoutedPath))
            points.Add(new Point((x - world.X) * scale, (y - world.Y) * scale));

        canvas.Children.Add(new Polyline
        {
            Points = points,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = Math.Max(1.5, WaveguideWidthMicrometers * scale),
            StrokeJoin = PenLineJoin.Round,
        });
    }

    private static (double X, double Y) PointOnSegment(CAP_Core.Routing.PathSegment segment, double t)
    {
        if (segment is BendSegment bend)
        {
            double sign = Math.Sign(bend.SweepAngleDegrees);
            if (sign == 0) sign = 1;
            double angleRad = (bend.StartAngleDegrees + bend.SweepAngleDegrees * t) * Math.PI / 180;
            return (bend.Center.X + bend.RadiusMicrometers * Math.Cos(angleRad - Math.PI / 2 * sign),
                    bend.Center.Y + bend.RadiusMicrometers * Math.Sin(angleRad - Math.PI / 2 * sign));
        }

        return (segment.StartPoint.X + (segment.EndPoint.X - segment.StartPoint.X) * t,
                segment.StartPoint.Y + (segment.EndPoint.Y - segment.StartPoint.Y) * t);
    }
}
