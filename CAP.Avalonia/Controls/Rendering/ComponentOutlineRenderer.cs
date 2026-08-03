using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media;
using CAP.Avalonia.Controls.Canvas.ComponentPreview;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Core;

namespace CAP.Avalonia.Controls.Rendering;

/// <summary>
/// Draws imported component outline polygons (e.g. from a GDS-imported PDK
/// component) in place of the plain rectangle body. Outline points are stored
/// in the component's local frame (µm, Y-down, relative to the unrotated bbox
/// top-left), so they map 1:1 onto the world-space footprint the rest of the
/// canvas renders in. Rotation reuses the exact mechanism of
/// <see cref="GdsPolygonRenderer"/>: geometry stays unrotated, the destination
/// rect is un-swapped via <see cref="GdsPolygonRenderer.GetUnrotatedDestRect"/>,
/// and <see cref="GdsPolygonRenderer.BuildRotationMatrix"/> rotates around the
/// footprint centre — the same centre and direction the rotate command uses
/// for pins.
/// </summary>
internal sealed class ComponentOutlineRenderer
{
    // Static readonly — never allocate per-frame (see GdsPolygonRenderer).
    // v1 styles every layer the same; Layer/DataType stay on the model.
    private static readonly IBrush FillBrush = new SolidColorBrush(Color.FromArgb(46, 100, 160, 220));
    private static readonly Pen OutlinePen = new(new SolidColorBrush(Color.FromArgb(160, 100, 160, 220)), 1);

    /// <summary>
    /// Geometry cache keyed by the outline list instance. All placed instances of
    /// one template share that list (see <c>ComponentTemplates.CreateFromTemplate</c>),
    /// so geometry is built once per component type, never per frame; the weak
    /// table drops the entry when the template is unloaded.
    /// </summary>
    private readonly ConditionalWeakTable<IReadOnlyList<OutlinePolygon>, StreamGeometry[]> _geometryCache = new();

    /// <summary>
    /// Draws <paramref name="outlines"/> for <paramref name="comp"/>. Caller must
    /// guarantee a non-empty list — the fallback rectangle path lives in
    /// <see cref="ComponentRenderer"/>.
    /// </summary>
    public void Draw(DrawingContext context, ComponentViewModel comp, IReadOnlyList<OutlinePolygon> outlines, bool isDimmed) =>
        Draw(context, comp.X, comp.Y, comp.Width, comp.Height,
            comp.Component.RotationDegrees, outlines, isDimmed);

    /// <summary>
    /// Pose-based overload for callers that have no <see cref="ComponentViewModel"/>:
    /// group children are rendered straight from their core component pose
    /// (<see cref="ComponentRenderer"/> flattens groups itself). Caller must guarantee
    /// a non-empty list — the fallback rectangle path lives in
    /// <see cref="ComponentRenderer"/>.
    /// </summary>
    public void Draw(DrawingContext context, double x, double y, double width, double height,
        double rotationDegrees, IReadOnlyList<OutlinePolygon> outlines, bool isDimmed)
    {
        var geometries = _geometryCache.GetValue(outlines, BuildGeometries);

        double centerX = x + width / 2.0;
        double centerY = y + height / 2.0;
        var destRect = GdsPolygonRenderer.GetUnrotatedDestRect(x, y, width, height, rotationDegrees);
        var transform = Matrix.CreateTranslation(destRect.X, destRect.Y)
                      * GdsPolygonRenderer.BuildRotationMatrix(rotationDegrees, centerX, centerY);

        using (context.PushTransform(transform))
        using (context.PushOpacity(isDimmed ? 128.0 / 255.0 : 1.0))
        {
            foreach (var geometry in geometries)
                context.DrawGeometry(FillBrush, OutlinePen, geometry);
        }
    }

    /// <summary>
    /// Transforms one outline point from the component's local frame to world
    /// coordinates for the given component pose — the same mapping
    /// <see cref="Draw"/> applies through the pushed transform. Exposed as
    /// <c>internal</c> to allow transform-math unit tests.
    /// </summary>
    internal static Point TransformOutlinePoint(
        OutlinePoint point,
        double compX, double compY,
        double compWidth, double compHeight,
        double rotationDegrees)
    {
        var destRect = GdsPolygonRenderer.GetUnrotatedDestRect(compX, compY, compWidth, compHeight, rotationDegrees);
        var rotation = GdsPolygonRenderer.BuildRotationMatrix(
            rotationDegrees, compX + compWidth / 2.0, compY + compHeight / 2.0);
        return new Point(destRect.X + point.X, destRect.Y + point.Y).Transform(rotation);
    }

    /// <summary>
    /// World-space points of one outline polygon for the given component pose.
    /// The ring stays closed: with the GDS convention (first point repeated at
    /// the end) the first and last world points coincide.
    /// </summary>
    internal static Point[] ComputeWorldPoints(
        OutlinePolygon polygon,
        double compX, double compY,
        double compWidth, double compHeight,
        double rotationDegrees)
    {
        var points = new Point[polygon.Points.Count];
        for (int i = 0; i < polygon.Points.Count; i++)
            points[i] = TransformOutlinePoint(polygon.Points[i], compX, compY, compWidth, compHeight, rotationDegrees);
        return points;
    }

    // One geometry per polygon (mirrors GdsPolygonRenderer): a single multi-figure
    // geometry would punch EvenOdd holes where overlapping layers coincide.
    private static StreamGeometry[] BuildGeometries(IReadOnlyList<OutlinePolygon> outlines)
    {
        var geometries = new List<StreamGeometry>(outlines.Count);
        foreach (var polygon in outlines)
        {
            if (polygon.Points.Count < 2)
                continue;

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(polygon.Points[0].X, polygon.Points[0].Y), true);
                for (int i = 1; i < polygon.Points.Count; i++)
                    ctx.LineTo(new Point(polygon.Points[i].X, polygon.Points[i].Y));
                ctx.EndFigure(true);
            }
            geometries.Add(geometry);
        }
        return geometries.ToArray();
    }
}
