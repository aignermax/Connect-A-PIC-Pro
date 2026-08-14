namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// Edge, coordinate, and interval helpers shared by the detection passes of
/// <see cref="GdsPinDetector"/>. Split out to keep the main detector file
/// within the architecture file-size limit.
/// </summary>
public static partial class GdsPinDetector
{
    /// <summary>App-space X: 0 at the left edge of the bounding box.</summary>
    private static double ToAppX(double gdsX, GdsBoundingBox bbox) => gdsX - bbox.MinX;

    /// <summary>App-space Y: 0 at the TOP edge (GDS MaxY), growing downward.</summary>
    private static double ToAppY(double gdsY, GdsBoundingBox bbox) => bbox.MaxY - gdsY;

    /// <summary>
    /// Outward normal of a bounding-box edge in the app angle convention
    /// (0° = east, 90° = down, 180° = west, 270° = up in the Y-down plane).
    /// </summary>
    private static double OutwardAngleDegrees(CellEdge edge) => edge switch
    {
        CellEdge.Left => 180.0,
        CellEdge.Top => 270.0,
        CellEdge.Right => 0.0,
        CellEdge.Bottom => 90.0,
        _ => throw new ArgumentOutOfRangeException(nameof(edge), edge, "Unknown cell edge."),
    };

    /// <summary>
    /// The bounding-box edge nearest to <paramref name="point"/>. Ties resolve in
    /// <see cref="CellEdge"/> declaration order (left, top, right, bottom).
    /// </summary>
    private static CellEdge NearestEdge(GdsPoint point, GdsBoundingBox bbox)
    {
        var best = CellEdge.Left;
        double bestDistance = Math.Abs(point.X - bbox.MinX);

        double top = Math.Abs(bbox.MaxY - point.Y);
        if (top < bestDistance) { best = CellEdge.Top; bestDistance = top; }

        double right = Math.Abs(bbox.MaxX - point.X);
        if (right < bestDistance) { best = CellEdge.Right; bestDistance = right; }

        double bottom = Math.Abs(point.Y - bbox.MinY);
        if (bottom < bestDistance) { best = CellEdge.Bottom; }

        return best;
    }

    /// <summary>
    /// Returns the edge whose line both segment endpoints lie on (within
    /// <paramref name="tolerance"/>), or null. Edges are checked in declaration
    /// order, so a degenerate corner segment can match at most one edge.
    /// </summary>
    private static CellEdge? TouchingEdge(GdsPoint p1, GdsPoint p2, GdsBoundingBox bbox, double tolerance)
    {
        if (Math.Abs(p1.X - bbox.MinX) <= tolerance && Math.Abs(p2.X - bbox.MinX) <= tolerance)
            return CellEdge.Left;
        if (Math.Abs(p1.Y - bbox.MaxY) <= tolerance && Math.Abs(p2.Y - bbox.MaxY) <= tolerance)
            return CellEdge.Top;
        if (Math.Abs(p1.X - bbox.MaxX) <= tolerance && Math.Abs(p2.X - bbox.MaxX) <= tolerance)
            return CellEdge.Right;
        if (Math.Abs(p1.Y - bbox.MinY) <= tolerance && Math.Abs(p2.Y - bbox.MinY) <= tolerance)
            return CellEdge.Bottom;
        return null;
    }

    /// <summary>Merges intervals that overlap or are separated by at most <paramref name="tolerance"/>.</summary>
    private static List<(double Start, double End)> MergeIntervals(
        List<(double Start, double End)> intervals, double tolerance)
    {
        intervals.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(double Start, double End)>();
        foreach (var (start, end) in intervals)
        {
            if (merged.Count > 0 && start <= merged[^1].End + tolerance)
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, end));
            else
                merged.Add((start, end));
        }
        return merged;
    }

    /// <summary>Reconstructs the GDS-space midpoint of a touch interval on an edge.</summary>
    private static GdsPoint MidpointOnEdge(CellEdge edge, double along, GdsBoundingBox bbox) => edge switch
    {
        CellEdge.Left => new GdsPoint(bbox.MinX, along),
        CellEdge.Right => new GdsPoint(bbox.MaxX, along),
        CellEdge.Top => new GdsPoint(along, bbox.MaxY),
        CellEdge.Bottom => new GdsPoint(along, bbox.MinY),
        _ => throw new ArgumentOutOfRangeException(nameof(edge), edge, "Unknown cell edge."),
    };

    /// <summary>True when a label anchor lies within tolerance of the touch midpoint.</summary>
    private static bool IsCoveredByLabel(GdsPoint midpoint, List<GdsPoint> labelAnchors, double tolerance)
    {
        foreach (var anchor in labelAnchors)
        {
            double dx = anchor.X - midpoint.X;
            double dy = anchor.Y - midpoint.Y;
            if (dx * dx + dy * dy <= tolerance * tolerance)
                return true;
        }
        return false;
    }

    private static bool ContainsLayer(
        IReadOnlyList<(int Layer, int Datatype)> layers, int layer, int datatype)
    {
        foreach (var (l, d) in layers)
        {
            if (l == layer && d == datatype)
                return true;
        }
        return false;
    }
}
