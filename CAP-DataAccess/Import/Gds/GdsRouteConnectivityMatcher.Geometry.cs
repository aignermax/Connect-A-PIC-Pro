namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// Pin↔polygon geometry predicates for <see cref="GdsRouteConnectivityMatcher"/>:
/// inside-or-near-outline touch test, even-odd point-in-polygon, and
/// point-to-segment distance.
/// </summary>
internal static partial class GdsRouteConnectivityMatcher
{
    /// <summary>
    /// True when the pin point lies inside the polygon (even-odd rule) or
    /// within <paramref name="toleranceUm"/> of any outline segment.
    /// </summary>
    private static bool Touches(GdsOutlinePolygon polygon, GdsAbsolutePin pin, double toleranceUm)
    {
        var points = polygon.Points;
        if (points.Count == 0)
            return false;
        if (PointInPolygon(points, pin.XUm, pin.YUm))
            return true;

        double toleranceSquared = toleranceUm * toleranceUm;
        for (int i = 0; i < points.Count; i++)
        {
            if (DistanceToSegmentSquared(pin.XUm, pin.YUm, points[i], points[(i + 1) % points.Count])
                <= toleranceSquared)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True when two pin points sit within <paramref name="toleranceUm"/> of
    /// each other (Euclidean): an own-export top-cell port label is stamped
    /// exactly on the coupler pin, so after F2-formatting and db-unit
    /// quantization the two points land far inside the pin-touch tolerance.
    /// </summary>
    private static bool IsCoincident(GdsAbsolutePin a, GdsAbsolutePin b, double toleranceUm)
    {
        double dx = a.XUm - b.XUm;
        double dy = a.YUm - b.YUm;
        return (dx * dx) + (dy * dy) <= toleranceUm * toleranceUm;
    }

    /// <summary>Even-odd point-in-polygon (ray cast towards +X; boundary hits count as inside via the outline distance).</summary>
    private static bool PointInPolygon(IReadOnlyList<GdsOutlinePoint> polygon, double x, double y)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var pi = polygon[i];
            var pj = polygon[j];
            if ((pi.Y > y) != (pj.Y > y)
                && x < ((pj.X - pi.X) * (y - pi.Y) / (pj.Y - pi.Y)) + pi.X)
            {
                inside = !inside;
            }
        }
        return inside;
    }

    private static double DistanceToSegmentSquared(
        double px, double py, GdsOutlinePoint a, GdsOutlinePoint b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double lengthSquared = (dx * dx) + (dy * dy);
        double t = lengthSquared == 0
            ? 0
            : Math.Clamp(((px - a.X) * dx + (py - a.Y) * dy) / lengthSquared, 0, 1);
        double cx = a.X + (t * dx) - px;
        double cy = a.Y + (t * dy) - py;
        return (cx * cx) + (cy * cy);
    }
}
