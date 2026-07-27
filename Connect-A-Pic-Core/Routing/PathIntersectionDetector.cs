namespace CAP_Core.Routing;

/// <summary>
/// Geometric intersection checks on routed paths: self-intersection of a single path
/// (loops, teardrops) and crossings between two paths. Paths are sampled into
/// polylines (arcs included) and tested with exact segment-segment intersection.
/// </summary>
public static class PathIntersectionDetector
{
    /// <summary>Sampling step along segments in micrometers.</summary>
    private const double SampleStepMicrometers = 2.0;

    /// <summary>
    /// Distance below which two polyline points count as the same joint
    /// (adjacent samples sharing an endpoint are not intersections).
    /// </summary>
    private const double JointToleranceMicrometers = 0.05;

    /// <summary>
    /// Returns true when the path crosses itself (e.g. a full-circle loop or a
    /// teardrop produced by a fallback router).
    /// </summary>
    public static bool HasSelfIntersection(RoutedPath path)
    {
        var points = SamplePolyline(path);
        for (int i = 0; i < points.Count - 1; i++)
        {
            // Skip the immediate neighbor: consecutive polyline segments share a joint.
            for (int j = i + 2; j < points.Count - 1; j++)
            {
                if (SegmentsIntersect(points[i], points[i + 1], points[j], points[j + 1]))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Returns the minimum distance (µm) between two paths. 0 means they cross,
    /// touch, or run on top of each other. Intended for routes between distinct pin
    /// pairs (no shared endpoints); use it to assert crossing-freedom and clearance.
    /// </summary>
    public static double MinimumDistance(RoutedPath first, RoutedPath second)
    {
        var a = SamplePolyline(first);
        var b = SamplePolyline(second);
        double min = double.MaxValue;
        for (int i = 0; i < a.Count - 1; i++)
        {
            for (int j = 0; j < b.Count - 1; j++)
            {
                if (SegmentsIntersect(a[i], a[i + 1], b[j], b[j + 1]))
                    return 0;
                min = Math.Min(min, SegmentDistance(a[i], a[i + 1], b[j], b[j + 1]));
            }
        }
        return min == double.MaxValue ? 0 : min;
    }

    /// <summary>
    /// Minimum distance (µm) from a point to the path's sampled polyline. Works for any
    /// path length, including a path that degenerates to a single sample point — unlike
    /// <see cref="MinimumDistance"/>, which needs two samplable polylines and reports 0
    /// for a degenerate partner regardless of the real separation.
    /// </summary>
    /// <param name="path">The path whose polyline is measured.</param>
    /// <param name="x">X coordinate of the point in micrometers.</param>
    /// <param name="y">Y coordinate of the point in micrometers.</param>
    public static double DistanceToPoint(RoutedPath path, double x, double y)
    {
        var points = SamplePolyline(path);
        if (points.Count == 0)
            return double.MaxValue;

        double min = Distance(points[0], (x, y));
        for (int i = 0; i < points.Count - 1; i++)
            min = Math.Min(min, PointToSegment((x, y), points[i], points[i + 1]));
        return min;
    }

    /// <summary>
    /// Number of proper polyline crossings between the two paths. Touching endpoints and
    /// collinear overlaps do not count (<see cref="HaveCollinearOverlap"/> covers the
    /// latter). Used to verify that an automated edit does not add crossings to geometry
    /// that legitimately crosses already (e.g. a blocked-fallback sibling).
    /// </summary>
    /// <param name="first">First path.</param>
    /// <param name="second">Second path.</param>
    public static int CrossingCount(RoutedPath first, RoutedPath second)
    {
        var a = SamplePolyline(first);
        var b = SamplePolyline(second);
        if (a.Count < 2 || b.Count < 2 || !BoundsOverlap(a, b))
            return 0;

        int count = 0;
        for (int i = 0; i < a.Count - 1; i++)
        {
            for (int j = 0; j < b.Count - 1; j++)
            {
                if (SegmentsIntersect(a[i], a[i + 1], b[j], b[j + 1]))
                    count++;
            }
        }
        return count;
    }

    /// <summary>
    /// True when a polyline segment of one path runs collinearly on top of a segment of
    /// the other over more than the joint tolerance. Proper crossings are not overlaps;
    /// this catches routes sliding onto each other, which the strict-sign intersection
    /// test cannot see.
    /// </summary>
    /// <param name="first">First path.</param>
    /// <param name="second">Second path.</param>
    public static bool HaveCollinearOverlap(RoutedPath first, RoutedPath second)
    {
        var a = SamplePolyline(first);
        var b = SamplePolyline(second);
        if (a.Count < 2 || b.Count < 2 || !BoundsOverlap(a, b))
            return false;

        for (int i = 0; i < a.Count - 1; i++)
        {
            for (int j = 0; j < b.Count - 1; j++)
            {
                if (SegmentsOverlapCollinearly(a[i], a[i + 1], b[j], b[j + 1]))
                    return true;
            }
        }
        return false;
    }

    /// <summary>Maximum perpendicular deviation (µm) for two segments to count as collinear.</summary>
    private const double CollinearDeviationToleranceMicrometers = 0.01;

    /// <summary>
    /// True when segment q lies on the carrier line of segment p (within tolerance) and
    /// their projections onto that line share an interval longer than the joint tolerance.
    /// </summary>
    private static bool SegmentsOverlapCollinearly(
        (double X, double Y) p1, (double X, double Y) p2,
        (double X, double Y) q1, (double X, double Y) q2)
    {
        double length = Distance(p1, p2);
        if (length < JointToleranceMicrometers)
            return false;
        if (Math.Abs(Cross(p1, p2, q1)) / length > CollinearDeviationToleranceMicrometers ||
            Math.Abs(Cross(p1, p2, q2)) / length > CollinearDeviationToleranceMicrometers)
            return false;

        double dx = (p2.X - p1.X) / length;
        double dy = (p2.Y - p1.Y) / length;
        double t1 = (q1.X - p1.X) * dx + (q1.Y - p1.Y) * dy;
        double t2 = (q2.X - p1.X) * dx + (q2.Y - p1.Y) * dy;
        double overlapStart = Math.Max(0, Math.Min(t1, t2));
        double overlapEnd = Math.Min(length, Math.Max(t1, t2));
        return overlapEnd - overlapStart > JointToleranceMicrometers;
    }

    /// <summary>
    /// Returns true when the two paths properly cross each other. Cheaper than
    /// <see cref="MinimumDistance"/> for the common non-crossing case: disjoint
    /// bounding boxes are rejected first and no distances are computed. Touching
    /// endpoints do not count as crossings (only proper intersections do).
    /// </summary>
    public static bool Crosses(RoutedPath first, RoutedPath second)
    {
        var a = SamplePolyline(first);
        var b = SamplePolyline(second);
        if (a.Count < 2 || b.Count < 2 || !BoundsOverlap(a, b))
            return false;

        for (int i = 0; i < a.Count - 1; i++)
        {
            for (int j = 0; j < b.Count - 1; j++)
            {
                if (SegmentsIntersect(a[i], a[i + 1], b[j], b[j + 1]))
                    return true;
            }
        }
        return false;
    }

    /// <summary>True when the axis-aligned bounding boxes of the two polylines overlap.</summary>
    private static bool BoundsOverlap(
        List<(double X, double Y)> a, List<(double X, double Y)> b)
    {
        double aMinX = a.Min(p => p.X), aMaxX = a.Max(p => p.X);
        double aMinY = a.Min(p => p.Y), aMaxY = a.Max(p => p.Y);
        double bMinX = b.Min(p => p.X), bMaxX = b.Max(p => p.X);
        double bMinY = b.Min(p => p.Y), bMaxY = b.Max(p => p.Y);
        return aMinX <= bMaxX && bMinX <= aMaxX && aMinY <= bMaxY && bMinY <= aMaxY;
    }

    /// <summary>
    /// Returns true when the path enters the given axis-aligned rectangle: a sampled
    /// point lies strictly inside it or a polyline segment crosses one of its edges.
    /// Shrink the rectangle by a small tolerance when path endpoints legitimately sit
    /// on its boundary (pins are placed on component edges).
    /// </summary>
    public static bool IntersectsRectangle(
        RoutedPath path, double minX, double minY, double maxX, double maxY)
    {
        var points = SamplePolyline(path);
        for (int i = 0; i < points.Count - 1; i++)
        {
            if (SegmentIntersectsRectangle(points[i], points[i + 1], minX, minY, maxX, maxY))
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when the segment has an endpoint strictly inside the rectangle or
    /// properly crosses one of its four edges.
    /// </summary>
    private static bool SegmentIntersectsRectangle(
        (double X, double Y) a, (double X, double Y) b,
        double minX, double minY, double maxX, double maxY)
    {
        static bool Inside((double X, double Y) p, double x1, double y1, double x2, double y2)
            => p.X > x1 && p.X < x2 && p.Y > y1 && p.Y < y2;

        if (Inside(a, minX, minY, maxX, maxY) || Inside(b, minX, minY, maxX, maxY))
            return true;

        var corners = new (double X, double Y)[]
        {
            (minX, minY), (maxX, minY), (maxX, maxY), (minX, maxY),
        };
        for (int i = 0; i < 4; i++)
        {
            if (SegmentsIntersect(a, b, corners[i], corners[(i + 1) % 4]))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Samples the path's segments (straights and arcs) into one polyline.
    /// Joints closer than <see cref="JointToleranceMicrometers"/> are merged so
    /// touching segment ends never read as intersections.
    /// </summary>
    private static List<(double X, double Y)> SamplePolyline(RoutedPath path)
    {
        var points = new List<(double X, double Y)>();
        foreach (var segment in path.Segments)
        {
            foreach (var point in SampleSegment(segment))
            {
                if (points.Count == 0 || Distance(points[^1], point) > JointToleranceMicrometers)
                    points.Add(point);
            }
        }
        return points;
    }

    private static IEnumerable<(double X, double Y)> SampleSegment(PathSegment segment)
    {
        if (segment is BendSegment bend)
        {
            double sweepRad = bend.SweepAngleDegrees * Math.PI / 180;
            double arcLength = Math.Abs(sweepRad) * bend.RadiusMicrometers;
            int steps = Math.Max(4, (int)Math.Ceiling(arcLength / SampleStepMicrometers));
            double sign = Math.Sign(bend.SweepAngleDegrees) == 0 ? 1 : Math.Sign(bend.SweepAngleDegrees);
            double startRad = bend.StartAngleDegrees * Math.PI / 180;

            for (int i = 0; i <= steps; i++)
            {
                double angle = startRad + sweepRad * i / steps;
                yield return (
                    bend.Center.X + bend.RadiusMicrometers * Math.Cos(angle - Math.PI / 2 * sign),
                    bend.Center.Y + bend.RadiusMicrometers * Math.Sin(angle - Math.PI / 2 * sign));
            }
            yield break;
        }

        yield return (segment.StartPoint.X, segment.StartPoint.Y);
        yield return (segment.EndPoint.X, segment.EndPoint.Y);
    }

    /// <summary>
    /// Exact 2D segment intersection (proper crossings and overlaps). Shared joints
    /// were merged during sampling, so any remaining touch is a genuine intersection.
    /// </summary>
    private static bool SegmentsIntersect(
        (double X, double Y) p1, (double X, double Y) p2,
        (double X, double Y) q1, (double X, double Y) q2)
    {
        double d1 = Cross(q1, q2, p1);
        double d2 = Cross(q1, q2, p2);
        double d3 = Cross(p1, p2, q1);
        double d4 = Cross(p1, p2, q2);

        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
            ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
            return true;

        return false;
    }

    private static double Cross((double X, double Y) a, (double X, double Y) b, (double X, double Y) c)
        => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    /// <summary>Minimum distance between two non-crossing segments (endpoint-to-segment).</summary>
    private static double SegmentDistance(
        (double X, double Y) p1, (double X, double Y) p2,
        (double X, double Y) q1, (double X, double Y) q2)
    {
        return Math.Min(
            Math.Min(PointToSegment(p1, q1, q2), PointToSegment(p2, q1, q2)),
            Math.Min(PointToSegment(q1, p1, p2), PointToSegment(q2, p1, p2)));
    }

    /// <summary>Distance from a point to a segment.</summary>
    private static double PointToSegment(
        (double X, double Y) p, (double X, double Y) a, (double X, double Y) b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double lengthSquared = dx * dx + dy * dy;
        if (lengthSquared < 1e-12) return Distance(p, a);

        double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared, 0, 1);
        return Distance(p, (a.X + t * dx, a.Y + t * dy));
    }

    private static double Distance((double X, double Y) a, (double X, double Y) b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
