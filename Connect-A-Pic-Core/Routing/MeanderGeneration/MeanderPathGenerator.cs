namespace CAP_Core.Routing.MeanderGeneration;

/// <summary>
/// Stretches the route between two poses (point + tangent direction) to a prescribed
/// geometric length: the nominal smooth route is computed first, then the extra length
/// is folded into its longest straight section — as hairpin loops for large extras, or
/// as a double S-bend trim for extras smaller than one loop. The result uses the same
/// <see cref="StraightSegment"/>/<see cref="BendSegment"/> primitives the waveguide
/// router produces, so it can be handed to the exporters unchanged.
/// Pure and deterministic: identical inputs yield an identical path.
/// </summary>
public sealed partial class MeanderPathGenerator
{
    private const double GeometrySlackMicrometers = 1e-6;
    private const double BoundsSlackMicrometers = 1e-4;
    private const double PosePositionToleranceMicrometers = 1e-4;
    private const double PoseAngleToleranceDegrees = 1e-4;
    private const double RadiusSlackMicrometers = 1e-9;
    private const int MaxLoopCount = 4096;
    private const int BisectionIterations = 60;

    /// <summary>
    /// Generates a path from the start pose to the end pose whose geometric length is
    /// within tolerance of the target, all bends at or above the minimum radius, fully
    /// inside the bounds — or a typed failure explaining why that is impossible.
    /// </summary>
    public MeanderResult Generate(MeanderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateArguments(request);

        double tolerance = Math.Max(request.ToleranceMicrometers, GeometrySlackMicrometers);
        RoutedPath? nominal = BuildNominalPath(request);
        if (nominal == null || !PoseMatches(nominal, request))
        {
            return MeanderResult.Failure(
                MeanderFailureReason.EndpointsNotRoutableAtMinRadius,
                "No tangent-continuous route between the two poses exists at the given minimum bend radius.");
        }

        double nominalLength = nominal.TotalLengthMicrometers;
        if (request.TargetLengthMicrometers < nominalLength - tolerance)
        {
            return MeanderResult.Failure(
                MeanderFailureReason.TargetShorterThanDirectPath,
                "The target length is shorter than the direct route between the poses.");
        }

        if (!IsWithinBounds(nominal, request.Bounds))
        {
            return MeanderResult.Failure(
                MeanderFailureReason.BoundsTooSmallForMeander,
                "The bounding rectangle does not contain the direct route between the poses.");
        }

        if (Math.Abs(request.TargetLengthMicrometers - nominalLength) <= tolerance)
            return MeanderResult.Success(nominal);

        double extra = request.TargetLengthMicrometers - nominalLength;
        int hostIndex = IndexOfLongestStraight(nominal);
        if (hostIndex < 0)
        {
            return MeanderResult.Failure(
                MeanderFailureReason.BoundsTooSmallForMeander,
                "The direct route has no straight section that could host a meander.");
        }

        foreach (double side in new[] { 1.0, -1.0 })
        {
            RoutedPath? candidate = TryBuildMeander(nominal, hostIndex, extra,
                request.MinBendRadiusMicrometers, side);
            if (candidate != null && IsValidCandidate(candidate, request, tolerance))
                return MeanderResult.Success(candidate);
        }

        return MeanderResult.Failure(
            MeanderFailureReason.BoundsTooSmallForMeander,
            "No meander with the required extra length fits inside the bounding rectangle at the given minimum bend radius.");
    }

    private static RoutedPath BuildNominalPath(MeanderRequest request)
    {
        var path = new RoutedPath();
        double dx = request.EndX - request.StartX;
        double dy = request.EndY - request.StartY;
        double distance = Math.Sqrt(dx * dx + dy * dy);

        if (distance <= GeometrySlackMicrometers)
        {
            bool sameDirection = AngleDistanceDegrees(
                request.StartDirectionDegrees, request.EndDirectionDegrees) <= PoseAngleToleranceDegrees;
            if (sameDirection)
                return path;
        }
        else
        {
            double lineAngle = Normalize360(Math.Atan2(dy, dx) * 180.0 / Math.PI);
            if (AngleDistanceDegrees(lineAngle, request.StartDirectionDegrees) <= PoseAngleToleranceDegrees
                && AngleDistanceDegrees(lineAngle, request.EndDirectionDegrees) <= PoseAngleToleranceDegrees)
            {
                path.Segments.Add(new StraightSegment(
                    request.StartX, request.StartY, request.EndX, request.EndY, lineAngle));
                return path;
            }
        }

        new ManhattanRouter(request.MinBendRadiusMicrometers).Route(
            request.StartX, request.StartY, request.StartDirectionDegrees,
            request.EndX, request.EndY, request.EndDirectionDegrees, path);
        return path;
    }

    private static RoutedPath? TryBuildMeander(
        RoutedPath nominal, int hostIndex, double extraMicrometers, double radius, double side)
    {
        double hostLength = nominal.Segments[hostIndex].LengthMicrometers;
        double loopMinExtra = (2.0 * Math.PI - 4.0) * radius;

        if (extraMicrometers >= loopMinExtra)
        {
            int byLength = (int)Math.Floor(extraMicrometers / loopMinExtra);
            int byRoom = (int)Math.Floor((hostLength + GeometrySlackMicrometers) / (4.0 * radius));
            int loops = Math.Min(Math.Min(byLength, byRoom), MaxLoopCount);
            if (loops >= 1)
            {
                double height = (extraMicrometers / loops - loopMinExtra) / 2.0;
                return Assemble(nominal, hostIndex, loops * 4.0 * radius,
                    builder => EmitLoops(builder, loops, height, radius, side));
            }
        }

        // Extra smaller than one hairpin loop (or no room for one): a double S-bend
        // leaves the host axis and returns to it, absorbing exactly 4r·(sweep − sin sweep)
        // — which reaches loopMinExtra at sweep = 90°, so the two shapes meet seamlessly.
        if (extraMicrometers > loopMinExtra)
            return null;

        double sweepDeg = SolveDoubleSBendSweepDegrees(extraMicrometers, radius);
        double footprint = 4.0 * radius * Math.Sin(sweepDeg * Math.PI / 180.0);
        if (footprint > hostLength + GeometrySlackMicrometers)
            return null;

        return Assemble(nominal, hostIndex, footprint, builder =>
        {
            builder.AppendArc(sweepDeg * side, radius);
            builder.AppendArc(-sweepDeg * side, radius);
            builder.AppendArc(-sweepDeg * side, radius);
            builder.AppendArc(sweepDeg * side, radius);
        });
    }

    private static RoutedPath Assemble(
        RoutedPath nominal, int hostIndex, double meanderFootprint, Action<PosePathBuilder> emitMeander)
    {
        var host = nominal.Segments[hostIndex];
        double dirX = host.EndPoint.X - host.StartPoint.X;
        double dirY = host.EndPoint.Y - host.StartPoint.Y;
        double hostLength = Math.Sqrt(dirX * dirX + dirY * dirY);
        dirX /= hostLength;
        dirY /= hostLength;
        double headingDeg = Normalize360(Math.Atan2(dirY, dirX) * 180.0 / Math.PI);

        double prefix = Math.Max(0.0, (hostLength - meanderFootprint) / 2.0);
        var result = new RoutedPath();
        for (int i = 0; i < hostIndex; i++)
            result.Segments.Add(nominal.Segments[i]);

        var builder = new PosePathBuilder(result, host.StartPoint.X, host.StartPoint.Y, headingDeg);
        builder.AppendStraight(prefix);
        emitMeander(builder);
        builder.AppendStraight(hostLength - prefix - meanderFootprint);

        for (int i = hostIndex + 1; i < nominal.Segments.Count; i++)
            result.Segments.Add(nominal.Segments[i]);
        return result;
    }

    private static void EmitLoops(
        PosePathBuilder builder, int loops, double heightMicrometers, double radius, double side)
    {
        for (int i = 0; i < loops; i++)
        {
            builder.AppendArc(90.0 * side, radius);
            builder.AppendStraight(heightMicrometers);
            builder.AppendArc(-180.0 * side, radius);
            builder.AppendStraight(heightMicrometers);
            builder.AppendArc(90.0 * side, radius);
        }
    }

    /// <summary>
    /// Sweep for the four arcs of a double S-bend (out and back to the host axis) so
    /// that its length exceeds the straight chord it replaces by exactly
    /// <paramref name="extraMicrometers"/>: extra = 4r·(sweep − sin sweep) is strictly
    /// monotone, so bisection converges.
    /// </summary>
    private static double SolveDoubleSBendSweepDegrees(double extraMicrometers, double radius)
    {
        double lo = 0.0;
        double hi = Math.PI / 2.0;
        for (int i = 0; i < BisectionIterations; i++)
        {
            double mid = (lo + hi) / 2.0;
            if (4.0 * radius * (mid - Math.Sin(mid)) < extraMicrometers)
                lo = mid;
            else
                hi = mid;
        }

        return (lo + hi) / 2.0 * 180.0 / Math.PI;
    }
}
