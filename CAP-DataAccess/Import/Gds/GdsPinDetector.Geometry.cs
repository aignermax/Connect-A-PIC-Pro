namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// Pure geometry helpers for <see cref="GdsPinDetector"/>: outline segment
/// enumeration, even-odd point-in-polygon, point-to-segment distance, polygon
/// bounding boxes, and the outward segment normal in the app angle convention.
/// </summary>
public static partial class GdsPinDetector
{
    /// <summary>
    /// Offset (µm) of the interior probe point from a segment midpoint when
    /// deciding which segment normal points AWAY from the polygon interior.
    /// One nanometer (one database unit in a typical 1 nm grid): large enough
    /// to survive floating-point noise, small enough to stay inside the
    /// thinnest real geometry (a 0.5 µm waveguide core).
    /// </summary>
    private const double InteriorProbeOffsetUm = 0.001;

    /// <summary>
    /// The outward normal angle of the segment p1–p2 in the app convention
    /// (0° = east, 90° = down in the Y-down plane). "Outward" — away from the
    /// polygon interior — is decided winding-agnostically (GDS polygons come in
    /// both orientations): the segment midpoint is probed a nanometer to the
    /// segment's left; when that probe lands INSIDE the polygon the left normal
    /// points inward and is flipped. The GDS-space (Y-up) normal (nx, ny) then
    /// gets the same Y-flip the pin positions get: appAngle = atan2(−ny, nx) —
    /// the transform <see cref="GdsInstancePinProjector"/> applies to pin
    /// directions. The caller guarantees a non-zero-length segment (the probe
    /// skips them).
    /// </summary>
    private static double SegmentOutwardAngleDegrees(GdsPolygon polygon, GdsPoint p1, GdsPoint p2)
    {
        double dx = p2.X - p1.X;
        double dy = p2.Y - p1.Y;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        double nx = -dy / length;
        double ny = dx / length;
        var probe = new GdsPoint(
            ((p1.X + p2.X) / 2.0) + (nx * InteriorProbeOffsetUm),
            ((p1.Y + p2.Y) / 2.0) + (ny * InteriorProbeOffsetUm));
        if (PointInPolygon(polygon.Points, probe))
        {
            nx = -nx;
            ny = -ny;
        }
        return GdsInstancePinProjector.Normalize360(Math.Atan2(-ny, nx) * 180.0 / Math.PI);
    }

    /// <summary>Even-odd point-in-polygon (ray cast towards +X), GDS space.</summary>
    private static bool PointInPolygon(IReadOnlyList<GdsPoint> polygon, GdsPoint point)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var pi = polygon[i];
            var pj = polygon[j];
            if ((pi.Y > point.Y) != (pj.Y > point.Y)
                && point.X < ((pj.X - pi.X) * (point.Y - pi.Y) / (pj.Y - pi.Y)) + pi.X)
            {
                inside = !inside;
            }
        }
        return inside;
    }

    /// <summary>Squared distance from <paramref name="point"/> to the segment a–b.</summary>
    private static double DistanceToSegmentSquared(GdsPoint point, GdsPoint a, GdsPoint b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double lengthSquared = (dx * dx) + (dy * dy);
        double t = lengthSquared == 0
            ? 0
            : Math.Clamp(((point.X - a.X) * dx + (point.Y - a.Y) * dy) / lengthSquared, 0, 1);
        double cx = a.X + (t * dx) - point.X;
        double cy = a.Y + (t * dy) - point.Y;
        return (cx * cx) + (cy * cy);
    }

    /// <summary>
    /// Consecutive vertex pairs of a polygon. GDS polygons repeat the first point
    /// at the end, so the closing segment comes for free and the duplicated point
    /// only ever forms a zero-length segment (filtered later by the width
    /// bounds). If the polygon is not closed, the closing segment is added.
    /// </summary>
    private static IEnumerable<(GdsPoint P1, GdsPoint P2)> Segments(GdsPolygon polygon)
    {
        var points = polygon.Points;
        for (int i = 0; i + 1 < points.Count; i++)
            yield return (points[i], points[i + 1]);

        if (points.Count > 2 && !points[0].Equals(points[^1]))
            yield return (points[^1], points[0]);
    }

    /// <summary>Axis-aligned bounding box of a non-empty point list (GDS space).</summary>
    private static GdsBoundingBox BoundingBoxOf(IReadOnlyList<GdsPoint> points)
    {
        double minX = points[0].X, minY = points[0].Y;
        double maxX = points[0].X, maxY = points[0].Y;
        foreach (var point in points)
        {
            minX = Math.Min(minX, point.X);
            minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X);
            maxY = Math.Max(maxY, point.Y);
        }
        return new GdsBoundingBox(minX, minY, maxX, maxY);
    }
}
