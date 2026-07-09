namespace CAP_Core.Routing.CrossingInsertion;

/// <summary>
/// Pure geometry helpers for crossing-insertion: straight-segment intersection,
/// axis-aligned orthogonality checks and straight-stub length validation.
/// </summary>
public static class CrossingGeometry
{
    /// <summary>Parametric tolerance so endpoint touches (T-junctions) don't count as crossings.</summary>
    private const double EndpointEpsilon = 1e-6;

    /// <summary>Numerical tolerance for parallel-line detection.</summary>
    private const double ParallelEpsilon = 1e-9;

    /// <summary>
    /// Computes the interior intersection point of two straight segments.
    /// Returns false for parallel segments or when the intersection lies on
    /// (or beyond) either segment's endpoints.
    /// </summary>
    public static bool TryGetIntersection(
        StraightSegment first, StraightSegment second, out (double X, double Y) point)
    {
        point = default;
        double d1x = first.EndPoint.X - first.StartPoint.X;
        double d1y = first.EndPoint.Y - first.StartPoint.Y;
        double d2x = second.EndPoint.X - second.StartPoint.X;
        double d2y = second.EndPoint.Y - second.StartPoint.Y;

        double denominator = d1x * d2y - d1y * d2x;
        if (Math.Abs(denominator) < ParallelEpsilon) return false;

        double offsetX = second.StartPoint.X - first.StartPoint.X;
        double offsetY = second.StartPoint.Y - first.StartPoint.Y;
        double t = (offsetX * d2y - offsetY * d2x) / denominator;
        double u = (offsetX * d1y - offsetY * d1x) / denominator;

        if (t <= EndpointEpsilon || t >= 1 - EndpointEpsilon) return false;
        if (u <= EndpointEpsilon || u >= 1 - EndpointEpsilon) return false;

        point = (first.StartPoint.X + t * d1x, first.StartPoint.Y + t * d1y);
        return true;
    }

    /// <summary>
    /// Checks that the two travel directions form an axis-aligned right angle:
    /// one runs horizontally, the other vertically, each within the given
    /// angular tolerance. The PDK crossing component is strictly orthogonal
    /// and is never rotated, so diagonal right angles are rejected too.
    /// </summary>
    /// <param name="firstDirection">Unit travel direction of the first waveguide.</param>
    /// <param name="secondDirection">Unit travel direction of the second waveguide.</param>
    /// <param name="toleranceDegrees">Allowed deviation from the axis in degrees.</param>
    /// <param name="firstIsHorizontal">True when the first direction is the horizontal one.</param>
    public static bool IsAxisAlignedRightAngle(
        (double X, double Y) firstDirection,
        (double X, double Y) secondDirection,
        double toleranceDegrees,
        out bool firstIsHorizontal)
    {
        firstIsHorizontal = false;
        bool firstHorizontal = IsAxisAligned(firstDirection, horizontal: true, toleranceDegrees);
        bool firstVertical = IsAxisAligned(firstDirection, horizontal: false, toleranceDegrees);
        bool secondHorizontal = IsAxisAligned(secondDirection, horizontal: true, toleranceDegrees);
        bool secondVertical = IsAxisAligned(secondDirection, horizontal: false, toleranceDegrees);

        if (firstHorizontal && secondVertical)
        {
            firstIsHorizontal = true;
            return true;
        }
        return firstVertical && secondHorizontal;
    }

    /// <summary>
    /// Verifies the segment runs straight for at least <paramref name="requiredRunMicrometers"/>
    /// on both sides of the intersection point, so the crossing ports can dock cleanly.
    /// </summary>
    public static bool HasStraightRunAround(
        StraightSegment segment, (double X, double Y) point, double requiredRunMicrometers)
    {
        double toStart = Distance(point, segment.StartPoint);
        double toEnd = Distance(point, segment.EndPoint);
        return toStart >= requiredRunMicrometers && toEnd >= requiredRunMicrometers;
    }

    /// <summary>Returns the unit travel direction (start → end) of a straight segment.</summary>
    public static (double X, double Y) GetDirection(StraightSegment segment)
    {
        double dx = segment.EndPoint.X - segment.StartPoint.X;
        double dy = segment.EndPoint.Y - segment.StartPoint.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length < ParallelEpsilon) return (0, 0);
        return (dx / length, dy / length);
    }

    private static bool IsAxisAligned((double X, double Y) direction, bool horizontal, double toleranceDegrees)
    {
        double along = horizontal ? direction.X : direction.Y;
        double across = horizontal ? direction.Y : direction.X;
        if (Math.Abs(along) < ParallelEpsilon && Math.Abs(across) < ParallelEpsilon) return false;
        double deviationDegrees = Math.Abs(Math.Atan2(Math.Abs(across), Math.Abs(along))) * 180.0 / Math.PI;
        return deviationDegrees <= toleranceDegrees;
    }

    private static double Distance((double X, double Y) a, (double X, double Y) b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
