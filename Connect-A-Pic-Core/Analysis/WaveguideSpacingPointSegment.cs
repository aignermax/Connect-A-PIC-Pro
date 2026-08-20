using CAP_Core.Routing;

namespace CAP_Core.Analysis;

/// <summary>
/// Low-level point-to-segment and segment-intersection helpers used by
/// <see cref="WaveguideSpacingGeometry"/>.
/// </summary>
internal static class WaveguideSpacingPointSegment
{
    internal static (double Distance, (double X, double Y) ClosestPoint) DistancePointToSegment(
        (double X, double Y) point,
        StraightSegment segment)
    {
        double dx = segment.EndPoint.X - segment.StartPoint.X;
        double dy = segment.EndPoint.Y - segment.StartPoint.Y;
        double lengthSquared = dx * dx + dy * dy;

        if (lengthSquared == 0)
        {
            double dx0 = point.X - segment.StartPoint.X;
            double dy0 = point.Y - segment.StartPoint.Y;
            double distanceToStart = Math.Sqrt(dx0 * dx0 + dy0 * dy0);
            return (distanceToStart, segment.StartPoint);
        }

        double t = ((point.X - segment.StartPoint.X) * dx + (point.Y - segment.StartPoint.Y) * dy) / lengthSquared;
        t = Math.Clamp(t, 0.0, 1.0);

        double closestX = segment.StartPoint.X + t * dx;
        double closestY = segment.StartPoint.Y + t * dy;
        double distX = point.X - closestX;
        double distY = point.Y - closestY;
        double distance = Math.Sqrt(distX * distX + distY * distY);

        return (distance, (closestX, closestY));
    }

    internal static (double X, double Y)? StraightStraightIntersection(StraightSegment a, StraightSegment b)
    {
        double ax = a.EndPoint.X - a.StartPoint.X;
        double ay = a.EndPoint.Y - a.StartPoint.Y;
        double bx = b.EndPoint.X - b.StartPoint.X;
        double by = b.EndPoint.Y - b.StartPoint.Y;

        double denom = ax * by - ay * bx;
        if (Math.Abs(denom) < 1e-10)
            return null;

        double dx = b.StartPoint.X - a.StartPoint.X;
        double dy = b.StartPoint.Y - a.StartPoint.Y;

        double t = (dx * by - dy * bx) / denom;
        double u = (dx * ay - dy * ax) / denom;

        if (t < 0.0 || t > 1.0 || u < 0.0 || u > 1.0)
            return null;

        return (a.StartPoint.X + t * ax, a.StartPoint.Y + t * ay);
    }

    internal static bool PointsMatch((double X, double Y) a, (double X, double Y) b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return dx * dx + dy * dy <= WaveguideSpacingGeometry.EndpointMatchToleranceMicrometers * WaveguideSpacingGeometry.EndpointMatchToleranceMicrometers;
    }

    internal static (double X, double Y) Midpoint((double X, double Y) a, (double X, double Y) b)
    {
        return ((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0);
    }
}
