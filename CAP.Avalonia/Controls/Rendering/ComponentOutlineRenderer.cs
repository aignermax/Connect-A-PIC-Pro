using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media;
using CAP.Avalonia.Controls.Canvas.ComponentPreview;
using CAP.Avalonia.Services.GdsImport.LayerVisibility;
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
    // Geometry is cached per outline list (below); the per-layer brushes/pens come
    // from OutlineLayerPalette's static cache — never allocated per frame.
    // v2 styles per (layer, datatype); the v1 single blue survives as the palette's
    // waveguide-core entry (1, 0).

    /// <summary>
    /// Geometry cache keyed by the outline list instance. All placed instances of
    /// one template share that list (see <c>ComponentTemplates.CreateFromTemplate</c>),
    /// so geometry is built once per component type, never per frame; the weak
    /// table drops the entry when the template is unloaded.
    /// </summary>
    private readonly ConditionalWeakTable<IReadOnlyList<OutlinePolygon>, CachedGeometry[]> _geometryCache = new();

    /// <summary>Test seam (InternalsVisibleTo UnitTests): geometries actually issued to
    /// <see cref="DrawingContext"/> since the last <see cref="ResetDrawCounters"/> — the
    /// LOD perf guard asserts this against <see cref="CulledGeometryCount"/>.</summary>
    internal long IssuedGeometryCount { get; private set; }

    /// <summary>Test seam (InternalsVisibleTo UnitTests): geometries skipped by the
    /// per-polygon LOD cull since the last <see cref="ResetDrawCounters"/>.</summary>
    internal long CulledGeometryCount { get; private set; }

    /// <summary>Test seam (InternalsVisibleTo UnitTests): zeroes both draw counters.</summary>
    internal void ResetDrawCounters() => (IssuedGeometryCount, CulledGeometryCount) = (0, 0);

    /// <summary>
    /// Draws <paramref name="outlines"/> for <paramref name="comp"/>. Caller must
    /// guarantee a non-empty list — the fallback rectangle path lives in
    /// <see cref="ComponentRenderer"/>.
    /// </summary>
    /// <param name="zoom">Current canvas zoom, used only for the per-polygon LOD cull
    /// (see <see cref="RenderCulling.IsBelowOutlineLodThreshold"/>).</param>
    /// <param name="layerVisibility">Per-design layer view filter (issue #858);
    /// null renders every layer fully visible.</param>
    public void Draw(DrawingContext context, ComponentViewModel comp, IReadOnlyList<OutlinePolygon> outlines, bool isDimmed, double zoom,
        GdsLayerVisibilityState? layerVisibility = null) =>
        Draw(context, comp.X, comp.Y, comp.Width, comp.Height,
            comp.Component.RotationDegrees, outlines, isDimmed, zoom,
            comp.Component.UnrotatedWidthMicrometers, comp.Component.UnrotatedHeightMicrometers,
            layerVisibility);

    /// <summary>
    /// Pose-based overload for callers that have no <see cref="ComponentViewModel"/>:
    /// group children are rendered straight from their core component pose
    /// (<see cref="ComponentRenderer"/> flattens groups itself). Caller must guarantee
    /// a non-empty list — the fallback rectangle path lives in
    /// <see cref="ComponentRenderer"/>.
    /// </summary>
    /// <param name="zoom">Current canvas zoom, used only for the per-polygon LOD cull
    /// (see <see cref="RenderCulling.IsBelowOutlineLodThreshold"/>).</param>
    /// <param name="recordedUnrotatedWidth">Recorded pre-rotation footprint width (0 when
    /// never rotated or legacy); see <c>Component.UnrotatedWidthMicrometers</c>.</param>
    /// <param name="recordedUnrotatedHeight">Recorded pre-rotation footprint height.</param>
    /// <param name="layerVisibility">Per-design layer view filter (issue #858):
    /// polygons on hidden layers are skipped, faded layers draw with reduced
    /// opacity. Null renders every layer fully visible.</param>
    public void Draw(DrawingContext context, double x, double y, double width, double height,
        double rotationDegrees, IReadOnlyList<OutlinePolygon> outlines, bool isDimmed, double zoom,
        double recordedUnrotatedWidth = 0, double recordedUnrotatedHeight = 0,
        GdsLayerVisibilityState? layerVisibility = null)
    {
        var geometries = _geometryCache.GetValue(outlines, BuildGeometries);

        double centerX = x + width / 2.0;
        double centerY = y + height / 2.0;
        var destRect = GdsPolygonRenderer.GetUnrotatedDestRect(
            x, y, width, height, rotationDegrees, recordedUnrotatedWidth, recordedUnrotatedHeight);
        var transform = Matrix.CreateTranslation(destRect.X, destRect.Y)
                      * GdsPolygonRenderer.BuildRotationMatrix(rotationDegrees, centerX, centerY);

        using (context.PushTransform(transform))
        using (context.PushOpacity(isDimmed ? 128.0 / 255.0 : 1.0))
        {
            foreach (var cached in geometries)
            {
                // Pure view filter (#858): a hidden layer draws nothing, a faded
                // layer draws through an extra opacity push. Deliberately outside
                // the LOD counters — hiding is a user choice, not a perf cull.
                double layerOpacity = layerVisibility?.EffectiveOpacity(cached.Layer, cached.DataType) ?? 1.0;
                if (layerOpacity <= 0)
                    continue;

                // The pushed transform is rigid, so the local-frame bbox × zoom is a
                // conservative on-screen size: at full zoom-out on a huge import most
                // polygons are sub-pixel specks whose DrawGeometry call costs far more
                // than what they rasterize.
                if (RenderCulling.IsBelowOutlineLodThreshold(cached.Bounds.Width, cached.Bounds.Height, zoom))
                {
                    CulledGeometryCount++;
                    continue;
                }
                IssuedGeometryCount++;
                if (layerOpacity < 1.0)
                {
                    using (context.PushOpacity(layerOpacity))
                        context.DrawGeometry(cached.Fill, cached.Outline, cached.Geometry);
                }
                else
                {
                    context.DrawGeometry(cached.Fill, cached.Outline, cached.Geometry);
                }
            }
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
        double rotationDegrees,
        double recordedUnrotatedWidth = 0, double recordedUnrotatedHeight = 0)
    {
        var destRect = GdsPolygonRenderer.GetUnrotatedDestRect(
            compX, compY, compWidth, compHeight, rotationDegrees,
            recordedUnrotatedWidth, recordedUnrotatedHeight);
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
        double rotationDegrees,
        double recordedUnrotatedWidth = 0, double recordedUnrotatedHeight = 0)
    {
        var points = new Point[polygon.Points.Count];
        for (int i = 0; i < polygon.Points.Count; i++)
            points[i] = TransformOutlinePoint(
                polygon.Points[i], compX, compY, compWidth, compHeight, rotationDegrees,
                recordedUnrotatedWidth, recordedUnrotatedHeight);
        return points;
    }

    // One geometry per polygon (mirrors GdsPolygonRenderer): a single multi-figure
    // geometry would punch EvenOdd holes where overlapping layers coincide. The
    // local-frame bounding box rides along for the per-polygon LOD cull in Draw;
    // the palette style is resolved here, once per polygon, never per frame.
    private static CachedGeometry[] BuildGeometries(IReadOnlyList<OutlinePolygon> outlines)
    {
        var geometries = new List<CachedGeometry>(outlines.Count);
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
            var (fill, outline) = OutlineLayerPalette.OutlineStyleFor(polygon.Layer, polygon.DataType);
            geometries.Add(new CachedGeometry(geometry, ComputeLocalBounds(polygon), fill, outline,
                polygon.Layer, polygon.DataType));
        }
        return geometries.ToArray();
    }

    private static Rect ComputeLocalBounds(OutlinePolygon polygon)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var point in polygon.Points)
        {
            minX = Math.Min(minX, point.X);
            minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X);
            maxY = Math.Max(maxY, point.Y);
        }
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>One cached polygon: its geometry, the local-frame bounding box the
    /// per-polygon LOD cull scales by the current zoom, its per-layer style, and
    /// its source (layer, datatype) for the per-layer view filter (#858).</summary>
    private sealed class CachedGeometry
    {
        public CachedGeometry(StreamGeometry geometry, Rect bounds, IBrush fill, Pen outline,
            int layer, int dataType)
        {
            Geometry = geometry;
            Bounds = bounds;
            Fill = fill;
            Outline = outline;
            Layer = layer;
            DataType = dataType;
        }

        public StreamGeometry Geometry { get; }
        public Rect Bounds { get; }
        public IBrush Fill { get; }
        public Pen Outline { get; }
        public int Layer { get; }
        public int DataType { get; }
    }
}
