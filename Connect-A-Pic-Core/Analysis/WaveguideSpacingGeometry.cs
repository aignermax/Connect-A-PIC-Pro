using CAP_Core.Routing;

namespace CAP_Core.Analysis;

/// <summary>
/// Geometric helpers used by <see cref="WaveguideSpacingDetector"/> to compute
/// centerline distances and spatial bounds between waveguide path segments.
/// </summary>
internal static class WaveguideSpacingGeometry
{
    internal const double EndpointMatchToleranceMicrometers = 1e-6;
    internal const double DistanceToleranceMicrometers = 1e-9;

    internal static (double Distance, (double X, double Y) ClosestPoint) ComputeCenterlineDistance(
        PathSegment a,
        PathSegment b,
        double minSpacing)
    {
        return (a, b) switch
        {
            (StraightSegment sa, StraightSegment sb) => StraightStraightDistance(sa, sb),
            (BendSegment ba, StraightSegment sb) => BendStraightDistance(ba, sb, minSpacing),
            (StraightSegment sa, BendSegment bb) => BendStraightDistance(bb, sa, minSpacing),
            (BendSegment ba, BendSegment bb) => BendBendDistance(ba, bb, minSpacing),
            _ => (double.MaxValue, ((a.StartPoint.X + b.StartPoint.X) / 2.0, (a.StartPoint.Y + b.StartPoint.Y) / 2.0))
        };
    }

    internal static (double MinX, double MinY, double MaxX, double MaxY) GetPaddedBounds(
        PathSegment segment,
        double halfWidth,
        double minSpacing)
    {
        double pad = halfWidth + minSpacing;

        if (segment is BendSegment bend)
        {
            return (
                bend.Center.X - bend.RadiusMicrometers - pad,
                bend.Center.Y - bend.RadiusMicrometers - pad,
                bend.Center.X + bend.RadiusMicrometers + pad,
                bend.Center.Y + bend.RadiusMicrometers + pad);
        }

        return (
            Math.Min(segment.StartPoint.X, segment.EndPoint.X) - pad,
            Math.Min(segment.StartPoint.Y, segment.EndPoint.Y) - pad,
            Math.Max(segment.StartPoint.X, segment.EndPoint.X) + pad,
            Math.Max(segment.StartPoint.Y, segment.EndPoint.Y) + pad);
    }

    internal static bool SegmentsShareEndpoint(PathSegment a, PathSegment b)
    {
        return WaveguideSpacingPointSegment.PointsMatch(a.StartPoint, b.StartPoint)
            || WaveguideSpacingPointSegment.PointsMatch(a.StartPoint, b.EndPoint)
            || WaveguideSpacingPointSegment.PointsMatch(a.EndPoint, b.StartPoint)
            || WaveguideSpacingPointSegment.PointsMatch(a.EndPoint, b.EndPoint);
    }

    private static (double Distance, (double X, double Y) ClosestPoint) StraightStraightDistance(
        StraightSegment a,
        StraightSegment b)
    {
        var intersection = WaveguideSpacingPointSegment.StraightStraightIntersection(a, b);
        if (intersection.HasValue)
            return (0.0, intersection.Value);

        double ax = a.EndPoint.X - a.StartPoint.X;
        double ay = a.EndPoint.Y - a.StartPoint.Y;
        double bx = b.EndPoint.X - b.StartPoint.X;
        double by = b.EndPoint.Y - b.StartPoint.Y;

        double lengthSquaredA = ax * ax + ay * ay;
        double lengthSquaredB = bx * bx + by * by;

        if (lengthSquaredA == 0)
        {
            var (distance, projection) = WaveguideSpacingPointSegment.DistancePointToSegment(a.StartPoint, b);
            return (distance, projection);
        }

        if (lengthSquaredB == 0)
        {
            var (distance, projection) = WaveguideSpacingPointSegment.DistancePointToSegment(b.StartPoint, a);
            return (distance, projection);
        }

        double cross = ax * by - ay * bx;
        bool parallel = Math.Abs(cross) < 1e-10;

        if (parallel)
        {
            double aStartProj = a.StartPoint.X * ax + a.StartPoint.Y * ay;
            double aEndProj = a.EndPoint.X * ax + a.EndPoint.Y * ay;
            double bStartProj = b.StartPoint.X * ax + b.StartPoint.Y * ay;
            double bEndProj = b.EndPoint.X * ax + b.EndPoint.Y * ay;

            double aMin = Math.Min(aStartProj, aEndProj);
            double aMax = Math.Max(aStartProj, aEndProj);
            double bMin = Math.Min(bStartProj, bEndProj);
            double bMax = Math.Max(bStartProj, bEndProj);

            double overlapMin = Math.Max(aMin, bMin);
            double overlapMax = Math.Min(aMax, bMax);

            if (overlapMin < overlapMax)
            {
                double midProj = (overlapMin + overlapMax) / 2.0;
                double tA = (midProj - aStartProj) / lengthSquaredA;

                (double X, double Y) pointA = (a.StartPoint.X + tA * ax, a.StartPoint.Y + tA * ay);
                var (distance, pointB) = WaveguideSpacingPointSegment.DistancePointToSegment(pointA, b);
                return (distance, WaveguideSpacingPointSegment.Midpoint(pointA, pointB));
            }
        }

        double bestDistance = double.MaxValue;
        (double X, double Y) closestA = a.StartPoint;
        (double X, double Y) closestB = b.StartPoint;

        TryUpdateBest(ref bestDistance, ref closestA, ref closestB, a.StartPoint, b);
        TryUpdateBest(ref bestDistance, ref closestA, ref closestB, a.EndPoint, b);
        TryUpdateBest(ref bestDistance, ref closestA, ref closestB, b.StartPoint, a);
        TryUpdateBest(ref bestDistance, ref closestA, ref closestB, b.EndPoint, a);

        return (bestDistance, WaveguideSpacingPointSegment.Midpoint(closestA, closestB));
    }

    private static void TryUpdateBest(
        ref double bestDistance,
        ref (double X, double Y) closestA,
        ref (double X, double Y) closestB,
        (double X, double Y) point,
        StraightSegment segment)
    {
        var (distance, projection) = WaveguideSpacingPointSegment.DistancePointToSegment(point, segment);
        if (distance < bestDistance)
        {
            bestDistance = distance;
            closestA = point;
            closestB = projection;
        }
    }

    private static (double Distance, (double X, double Y) ClosestPoint) BendStraightDistance(
        BendSegment bend,
        StraightSegment straight,
        double minSpacing)
    {
        var samples = WaveguideSpacingSampler.SampleBend(bend, minSpacing);
        double bestDistance = double.MaxValue;
        (double X, double Y) bestBendPoint = bend.StartPoint;
        (double X, double Y) bestStraightPoint = straight.StartPoint;

        foreach (var point in samples)
        {
            var (distance, projection) = WaveguideSpacingPointSegment.DistancePointToSegment(point, straight);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestBendPoint = point;
                bestStraightPoint = projection;
            }
        }

        return (bestDistance, WaveguideSpacingPointSegment.Midpoint(bestBendPoint, bestStraightPoint));
    }

    private static (double Distance, (double X, double Y) ClosestPoint) BendBendDistance(
        BendSegment a,
        BendSegment b,
        double minSpacing)
    {
        var samplesA = WaveguideSpacingSampler.SampleBend(a, minSpacing);
        var samplesB = WaveguideSpacingSampler.SampleBend(b, minSpacing);

        double bestDistance = double.MaxValue;
        (double X, double Y) bestA = a.StartPoint;
        (double X, double Y) bestB = b.StartPoint;

        foreach (var pointA in samplesA)
        {
            foreach (var pointB in samplesB)
            {
                double dx = pointA.X - pointB.X;
                double dy = pointA.Y - pointB.Y;
                double distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestA = pointA;
                    bestB = pointB;
                }
            }
        }

        return (bestDistance, WaveguideSpacingPointSegment.Midpoint(bestA, bestB));
    }
}
