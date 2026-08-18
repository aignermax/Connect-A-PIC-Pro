namespace CAP_Core.Routing.MeanderGeneration;

/// <summary>
/// Validation half of <see cref="MeanderPathGenerator"/>: argument checks and the
/// geometric post-checks every emitted path must pass (length, bend-radius floor,
/// bounds, continuity, endpoint poses) before it may leave the generator.
/// </summary>
public sealed partial class MeanderPathGenerator
{
    private bool IsValidCandidate(RoutedPath path, MeanderRequest request, double tolerance)
    {
        if (!path.IsValid || !PoseMatches(path, request))
            return false;
        if (Math.Abs(path.TotalLengthMicrometers - request.TargetLengthMicrometers) > tolerance)
            return false;

        foreach (var segment in path.Segments)
        {
            if (segment is BendSegment bend
                && bend.RadiusMicrometers < request.MinBendRadiusMicrometers - RadiusSlackMicrometers)
                return false;
            if (!request.Bounds.Contains(PathSegmentBounds.Of(segment), BoundsSlackMicrometers))
                return false;
        }

        return true;
    }

    private static bool IsWithinBounds(RoutedPath path, MeanderBounds bounds)
        => path.Segments.All(s => bounds.Contains(PathSegmentBounds.Of(s), BoundsSlackMicrometers));

    private static bool PoseMatches(RoutedPath path, MeanderRequest request)
    {
        if (path.Segments.Count == 0)
        {
            double dx = request.EndX - request.StartX;
            double dy = request.EndY - request.StartY;
            return Math.Sqrt(dx * dx + dy * dy) <= PosePositionToleranceMicrometers;
        }

        var first = path.Segments[0];
        var last = path.Segments[^1];
        return Distance(first.StartPoint, (request.StartX, request.StartY)) <= PosePositionToleranceMicrometers
            && Distance(last.EndPoint, (request.EndX, request.EndY)) <= PosePositionToleranceMicrometers
            && AngleDistanceDegrees(first.StartAngleDegrees, request.StartDirectionDegrees) <= PoseAngleToleranceDegrees
            && AngleDistanceDegrees(last.EndAngleDegrees, request.EndDirectionDegrees) <= PoseAngleToleranceDegrees;
    }

    private static int IndexOfLongestStraight(RoutedPath path)
    {
        int index = -1;
        double longest = 0.0;
        for (int i = 0; i < path.Segments.Count; i++)
        {
            if (path.Segments[i] is StraightSegment straight
                && straight.LengthMicrometers > longest + GeometrySlackMicrometers)
            {
                index = i;
                longest = straight.LengthMicrometers;
            }
        }

        return index;
    }

    private static void ValidateArguments(MeanderRequest request)
    {
        if (!double.IsFinite(request.StartX) || !double.IsFinite(request.StartY)
            || !double.IsFinite(request.EndX) || !double.IsFinite(request.EndY)
            || !double.IsFinite(request.StartDirectionDegrees) || !double.IsFinite(request.EndDirectionDegrees)
            || !double.IsFinite(request.TargetLengthMicrometers) || !double.IsFinite(request.ToleranceMicrometers)
            || !double.IsFinite(request.MinBendRadiusMicrometers))
            throw new ArgumentException("All request values must be finite.", nameof(request));
        if (request.MinBendRadiusMicrometers <= 0)
            throw new ArgumentException("Minimum bend radius must be positive.", nameof(request));
        if (request.TargetLengthMicrometers < 0)
            throw new ArgumentException("Target length must not be negative.", nameof(request));
        if (request.ToleranceMicrometers < 0)
            throw new ArgumentException("Tolerance must not be negative.", nameof(request));
        if (request.Bounds.MaxX <= request.Bounds.MinX || request.Bounds.MaxY <= request.Bounds.MinY)
            throw new ArgumentException("Bounds must have positive width and height.", nameof(request));
    }

    private static double Distance((double X, double Y) a, (double X, double Y) b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double Normalize360(double angleDeg)
    {
        angleDeg %= 360.0;
        if (angleDeg < 0.0)
            angleDeg += 360.0;
        return angleDeg;
    }

    private static double AngleDistanceDegrees(double aDeg, double bDeg)
    {
        double delta = Normalize360(aDeg) - Normalize360(bDeg);
        if (delta < 0.0)
            delta += 360.0;
        return Math.Min(delta, 360.0 - delta);
    }
}
