namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// Converts a <see cref="GdsPath"/> (centerline + width — the dominant routing
/// representation in real PDK exports) into polygon outlines in the path's OWN
/// coordinate space: one oriented quad per centerline segment, spanning the
/// full path width. Callers transform the quad corners afterwards, exactly
/// like ordinary polygon points, so magnification and mirroring apply to the
/// stroked width for free.
///
/// Deliberate v1 approximations — the consumers are filled rendering,
/// pin touch-probing and route-network chaining, none of which need an exact
/// boolean outline:
/// <list type="bullet">
/// <item>Bend joins are not mitered: consecutive quads overlap on the inside
/// of a corner and leave a small notch on the outside. Overlap is harmless for
/// filling and containment tests, and the quads still touch, so network
/// chaining sees one connected stroke.</item>
/// <item>PATHTYPE 1 (round caps) is treated like PATHTYPE 2: the end segments
/// are extended by half the width. A half-disc approximation would add many
/// vertices for the same touch behavior and no visible rendering benefit.</item>
/// </list>
/// </summary>
public static class GdsPathOutliner
{
    private const int PathTypeFlush = 0;

    /// <summary>
    /// The outline quads of <paramref name="path"/>: one closed rectangle ring
    /// (first point repeated, like GDS BOUNDARY elements) per non-degenerate
    /// centerline segment, carrying the path's layer and datatype. Zero-length
    /// segments are dropped. A single-point path yields one axis-aligned
    /// width×width square for PATHTYPE 1/2 (the cap extends half a width beyond
    /// the endpoint even without any run length) and nothing for flush ends.
    /// A zero-width path yields nothing: the reader stores |WIDTH| (never
    /// negative), so 0 means the record was absent — such a path encloses no
    /// area, and fabricating a hairline would invent geometry the file does
    /// not contain (and could fabricate route connections from it).
    /// </summary>
    public static IReadOnlyList<GdsPolygon> Outline(GdsPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.WidthMicrometers <= 0)
            return Array.Empty<GdsPolygon>();

        var centerline = WithoutZeroLengthSegments(path.Points);
        double halfWidth = path.WidthMicrometers / 2.0;
        bool capped = path.PathType != PathTypeFlush;

        if (centerline.Count == 0)
            return Array.Empty<GdsPolygon>();
        if (centerline.Count == 1)
        {
            return capped
                ? new[] { SquareCap(path, centerline[0], halfWidth) }
                : Array.Empty<GdsPolygon>();
        }

        var quads = new List<GdsPolygon>(centerline.Count - 1);
        for (int i = 0; i + 1 < centerline.Count; i++)
        {
            double startExtension = capped && i == 0 ? halfWidth : 0.0;
            double endExtension = capped && i == centerline.Count - 2 ? halfWidth : 0.0;
            quads.Add(SegmentQuad(path, centerline[i], centerline[i + 1], halfWidth, startExtension, endExtension));
        }
        return quads;
    }

    /// <summary>
    /// A cell's own drawn geometry as polygons: BOUNDARY elements verbatim,
    /// PATH elements expanded through <see cref="Outline"/>, everything else
    /// (texts, references) skipped. Collectors that read raw cell elements use
    /// this so path-drawn routing is never invisible to them.
    /// </summary>
    public static IEnumerable<GdsPolygon> ExpandDrawnGeometry(IEnumerable<GdsElement> elements)
    {
        ArgumentNullException.ThrowIfNull(elements);
        return elements.SelectMany(element => element switch
        {
            GdsPolygon polygon => new[] { polygon },
            GdsPath path => Outline(path),
            _ => Enumerable.Empty<GdsPolygon>(),
        });
    }

    /// <summary>Centerline with consecutive duplicate points collapsed (zero-length segments carry no stroke).</summary>
    private static List<GdsPoint> WithoutZeroLengthSegments(IReadOnlyList<GdsPoint> points)
    {
        var cleaned = new List<GdsPoint>(points.Count);
        foreach (var point in points)
        {
            if (cleaned.Count == 0 || !cleaned[^1].Equals(point))
                cleaned.Add(point);
        }
        return cleaned;
    }

    /// <summary>
    /// The oriented full-width rectangle around one centerline segment, with the
    /// start/end pushed outward along the segment direction by the given cap
    /// extensions (half the width for PATHTYPE 1/2 end segments, 0 otherwise).
    /// </summary>
    private static GdsPolygon SegmentQuad(
        GdsPath path, GdsPoint from, GdsPoint to, double halfWidth,
        double startExtension, double endExtension)
    {
        double dx = to.X - from.X;
        double dy = to.Y - from.Y;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        double ux = dx / length;
        double uy = dy / length;
        double nx = -uy * halfWidth;
        double ny = ux * halfWidth;

        var start = new GdsPoint(from.X - (ux * startExtension), from.Y - (uy * startExtension));
        var end = new GdsPoint(to.X + (ux * endExtension), to.Y + (uy * endExtension));
        return new GdsPolygon
        {
            Layer = path.Layer,
            DataType = path.DataType,
            Points = new[]
            {
                new GdsPoint(start.X + nx, start.Y + ny),
                new GdsPoint(end.X + nx, end.Y + ny),
                new GdsPoint(end.X - nx, end.Y - ny),
                new GdsPoint(start.X - nx, start.Y - ny),
                new GdsPoint(start.X + nx, start.Y + ny),
            },
        };
    }

    /// <summary>The axis-aligned width×width cap square around a single-point capped path.</summary>
    private static GdsPolygon SquareCap(GdsPath path, GdsPoint center, double halfWidth) => new()
    {
        Layer = path.Layer,
        DataType = path.DataType,
        Points = new[]
        {
            new GdsPoint(center.X - halfWidth, center.Y - halfWidth),
            new GdsPoint(center.X + halfWidth, center.Y - halfWidth),
            new GdsPoint(center.X + halfWidth, center.Y + halfWidth),
            new GdsPoint(center.X - halfWidth, center.Y + halfWidth),
            new GdsPoint(center.X - halfWidth, center.Y - halfWidth),
        },
    };
}
