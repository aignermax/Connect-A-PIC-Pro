using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CAP_DataAccess.Import.Gds;

namespace UnitTests.UI;

/// <summary>
/// Renders the exported GDS of the round-trip scenario as an independent
/// "what is actually in the file" ground-truth pane: the file is read back with
/// Lunima's OWN <see cref="GdsReader"/> and flattened with
/// <see cref="GdsCellFlattener"/> — deliberately NOT via gdstk/klayout, so the
/// image cross-checks the app's reader, not a third-party one. Polygons are
/// drawn filled, in the canvas' coordinate convention (Y-down, µm; the GDS
/// Y-up axis is flipped here exactly like the exporter flips it on write), so
/// the pane can share the world window and scale of the canvas renders.
/// <para>
/// Waveguides are distinguished from device geometry by provenance, not by
/// layer: the export flattens the routed waveguides INTO the top cell (the
/// Python cross-check in <c>GdsHighestLevelRoundTripTests</c> pins the top cell
/// at exactly one reference per component), so every direct top-cell polygon is
/// routed-waveguide shape, while everything pulled in through a reference is
/// device-cell geometry. Layer alone could not separate them — the foundry
/// device bodies sit on the same waveguide layer (1,0) as the routes.
/// </para>
/// </summary>
internal static class GdsGroundTruthRenderer
{
    // Muted palette on the app's dark canvas background (#1E1E1E), chosen so the
    // three buckets stay distinguishable at a glance and the pixel assertions can
    // separate them by channel: bronze is red-dominant, the device fills are
    // blue-dominant, the background is neutral dark.
    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));

    /// <summary>Routed waveguides (direct top-cell polygons): muted bronze, echoes the canvas' orange routes.</summary>
    private static readonly IBrush WaveguideBrush = new SolidColorBrush(Color.FromRgb(0xB0, 0x78, 0x40));

    /// <summary>Device-cell polygons on the waveguide layer (1,0): muted steel blue.</summary>
    private static readonly IBrush DeviceSiliconBrush = new SolidColorBrush(Color.FromRgb(0x5E, 0x7B, 0xA6));

    /// <summary>Device-cell polygons on every other layer (foundry detail layers): muted plum.</summary>
    private static readonly IBrush DeviceOtherBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0x5E, 0x7A));

    /// <summary>Pin-label anchor markers (GDS TEXT elements): muted gray dots.</summary>
    private static readonly IBrush TextAnchorBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xA8));

    /// <summary>Pixel radius of a pin-label anchor dot.</summary>
    private const double TextAnchorRadiusPx = 2.0;

    /// <summary>
    /// Renders the flattened top cell <paramref name="topCellName"/> of
    /// <paramref name="library"/> into a new bitmap of <paramref name="size"/>.
    /// The world (µm, Y-down) region <paramref name="world"/> maps onto the
    /// bitmap exactly — the same transform the canvas renders use, so identical
    /// world windows produce pixel-aligned images.
    /// </summary>
    public static RenderTargetBitmap RenderTopCell(
        GdsLibrary library,
        GdsCellFlattener flattener,
        string topCellName,
        Rect world,
        PixelSize size)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(flattener);
        double scale = size.Width / world.Width;

        // Bucket by provenance + layer; the draw order (foundry detail layers,
        // then device silicon, then waveguides on top) keeps the routed network
        // legible where it meets the device bodies.
        var deviceOther = new List<GdsPolygon>();
        var deviceSilicon = new List<GdsPolygon>();
        var textAnchors = new List<GdsPoint>();

        foreach (var instance in flattener.GetInstanceTree(topCellName))
        {
            var flattened = flattener.Flatten(instance.CellName);
            foreach (var polygon in flattened.Polygons)
            {
                var moved = polygon with
                {
                    Points = polygon.Points.Select(p => ApplyInstance(p, instance)).ToList(),
                };
                (IsWaveguideLayer(moved) ? deviceSilicon : deviceOther).Add(moved);
            }
            foreach (var text in flattened.Texts)
                textAnchors.Add(ApplyInstance(text.Position, instance));
        }

        var waveguides = library.Cells[topCellName].Elements.OfType<GdsPolygon>().ToList();

        var bitmap = new RenderTargetBitmap(size);
        using (var context = bitmap.CreateDrawingContext())
        {
            context.FillRectangle(BackgroundBrush, new Rect(0, 0, size.Width, size.Height));
            foreach (var polygon in deviceOther)
                Fill(context, polygon, DeviceOtherBrush, world, scale);
            foreach (var polygon in deviceSilicon)
                Fill(context, polygon, DeviceSiliconBrush, world, scale);
            foreach (var polygon in waveguides)
                Fill(context, polygon, WaveguideBrush, world, scale);
            foreach (var anchor in textAnchors)
                DrawAnchor(context, anchor, world, scale);
        }
        return bitmap;
    }

    /// <summary>The waveguide/silicon layer pair the export routes on.</summary>
    private static bool IsWaveguideLayer(GdsPolygon polygon) => polygon.Layer == 1 && polygon.DataType == 0;

    /// <summary>
    /// Applies one instance transform to a point of the referenced cell: GDS
    /// semantics are magnification and X-reflection first, then the
    /// counter-clockwise rotation, then the translation — mirroring
    /// <c>GdsTransform.FromReference</c> (internal to CAP-DataAccess). Every
    /// instance of this scenario is an unrotated, unmagnified translation, but
    /// the general form costs nothing.
    /// </summary>
    private static GdsPoint ApplyInstance(GdsPoint point, GdsInstance instance)
    {
        double radians = instance.AngleDegrees * Math.PI / 180.0;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        double ySign = instance.Reflected ? -1.0 : 1.0;
        double m = instance.Magnification;
        return new GdsPoint(
            instance.Offset.X + m * (cos * point.X - ySign * sin * point.Y),
            instance.Offset.Y + m * (sin * point.X + ySign * cos * point.Y));
    }

    /// <summary>Fills one polygon (Y-up GDS coordinates) into the Y-down world window.</summary>
    private static void Fill(DrawingContext context, GdsPolygon polygon, IBrush brush, Rect world, double scale)
    {
        if (polygon.Points.Count < 2)
            return;
        var points = polygon.Points.Select(p => ToPixel(p, world, scale)).ToList();
        context.DrawGeometry(brush, null, new PolylineGeometry(points, isFilled: true));
    }

    /// <summary>Draws a pin-label anchor dot (Y-up GDS coordinates).</summary>
    private static void DrawAnchor(DrawingContext context, GdsPoint anchor, Rect world, double scale)
    {
        var center = ToPixel(anchor, world, scale);
        context.DrawEllipse(TextAnchorBrush, null, center, TextAnchorRadiusPx, TextAnchorRadiusPx);
    }

    /// <summary>
    /// Maps a Y-up GDS point to a bitmap pixel: the Y flip (worldY = −gdsY) is
    /// the inverse of the exporter's own canvas→GDS flip, so the pane lands in
    /// the canvas' world frame (Y-down, µm) and shares its window/scale.
    /// </summary>
    private static Point ToPixel(GdsPoint point, Rect world, double scale) =>
        new((point.X - world.X) * scale, (-point.Y - world.Y) * scale);
}
