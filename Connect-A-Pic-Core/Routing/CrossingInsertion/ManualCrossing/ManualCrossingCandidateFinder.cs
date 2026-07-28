using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Components.PinKinds;

namespace CAP_Core.Routing.CrossingInsertion.ManualCrossing;

/// <summary>
/// Computes Cut-tool guide lines and crossing-insertion candidates.
/// Guide lines are rays from axis-aligned optical pins; candidates are the
/// interior intersections of those rays with perpendicular straight waveguide
/// segments that leave enough straight run to dock a crossing component, with a bounding
/// box that is actually clear of other geometry. Manhattan-only in v1: arcs and diagonal
/// segments are skipped.
/// </summary>
public class ManualCrossingCandidateFinder
{
    /// <summary>Length used to extend a guide ray for intersection math (µm).</summary>
    private const double GuideRayLengthMicrometers = 1_000_000.0;

    /// <summary>Candidates whose bucketed position coincides are duplicates of the same spot (µm).</summary>
    private const double DuplicateToleranceMicrometers = 0.5;

    /// <summary>Allowed angular deviation of pins and segments from a cardinal axis (degrees).</summary>
    public double AxisToleranceDegrees { get; set; } = 1.0;

    /// <summary>
    /// Builds guide lines for all axis-aligned optical pins. Pins facing a
    /// non-cardinal direction produce no guide line (the PDK crossing component
    /// is strictly orthogonal), and electrical pins are skipped.
    /// </summary>
    public IReadOnlyList<PinGuideLine> BuildGuideLines(IEnumerable<PhysicalPin> pins)
    {
        var guides = new List<PinGuideLine>();
        foreach (var pin in pins)
        {
            if (PinKindHelper.IsElectrical(pin)) continue;
            var direction = DirectionForAngle(pin.GetAbsoluteAngle());
            if (direction == null) continue;

            var (x, y) = pin.GetAbsolutePosition();
            bool isHorizontal = Math.Abs(direction.Value.X) > 0.5;
            guides.Add(new PinGuideLine(pin, (x, y), direction.Value, isHorizontal));
        }
        return guides;
    }

    /// <summary>
    /// Finds all insertion candidates: for each guide ray, the interior
    /// intersections with perpendicular straight segments of the given
    /// connections that keep at least <paramref name="requiredStraightRunMicrometers"/>
    /// of straight run on both sides of the crossing center and a clear bounding box.
    /// </summary>
    /// <param name="guideLines">Guide lines from <see cref="BuildGuideLines"/>.</param>
    /// <param name="connections">Connections whose routed paths are intersected.</param>
    /// <param name="requiredStraightRunMicrometers">
    /// Crossing half-extent plus stub clearance — the straight run each side of
    /// the intersection must offer so the crossing ports dock cleanly.
    /// </param>
    /// <param name="footprint">Grid clearance check; null skips it.</param>
    public IReadOnlyList<ManualCrossingCandidate> FindCandidates(
        IReadOnlyList<PinGuideLine> guideLines,
        IEnumerable<WaveguideConnection> connections,
        double requiredStraightRunMicrometers,
        FootprintClearance? footprint = null)
    {
        // Each guide's ray is independent of the segment being tested against, so it is
        // built once here rather than once per (segment, guide) pair in the loop below.
        var rays = BuildGuideRays(guideLines);
        var candidates = new List<ManualCrossingCandidate>();
        var seenBuckets = new HashSet<(long, long)>();

        foreach (var connection in connections)
        {
            if (connection.IsElectrical) continue;
            foreach (var segment in connection.GetPathSegments().OfType<StraightSegment>())
                CollectSegmentCandidates(candidates, seenBuckets, connection, segment,
                    rays, requiredStraightRunMicrometers, footprint);
        }
        return candidates;
    }

    private void CollectSegmentCandidates(
        List<ManualCrossingCandidate> candidates,
        HashSet<(long, long)> seenBuckets,
        WaveguideConnection connection,
        StraightSegment segment,
        List<(PinGuideLine Guide, StraightSegment Ray)> rays,
        double requiredRunMicrometers,
        FootprintClearance? footprint)
    {
        var segmentDirection = CrossingGeometry.GetDirection(segment);
        foreach (var (guide, ray) in rays)
        {
            if (guide.Pin.ParentComponent != null &&
                (connection.StartPin == guide.Pin || connection.EndPin == guide.Pin))
                continue;

            if (!CrossingGeometry.IsAxisAlignedRightAngle(
                    guide.Direction, segmentDirection, AxisToleranceDegrees, out bool guideIsHorizontal))
                continue;

            if (!CrossingGeometry.TryGetIntersection(ray, segment, out var point)) continue;
            if (DistanceFromOrigin(guide, point) < requiredRunMicrometers) continue;
            if (!CrossingGeometry.HasStraightRunAround(segment, point, requiredRunMicrometers)) continue;
            if (!IsFootprintClear(footprint, point, connection.Id)) continue;
            if (!TryMarkSeen(seenBuckets, point)) continue;

            candidates.Add(new ManualCrossingCandidate(
                connection, segment, guide, point, !guideIsHorizontal, segmentDirection));
        }
    }

    /// <summary>
    /// Resolves the candidate an interaction at <paramref name="point"/> should act on: a
    /// precomputed guide-intersection candidate within <paramref name="snapRadiusMicrometers"/>
    /// takes precedence (exact, predictable insertion point); otherwise the nearest eligible
    /// segment within the same radius yields a free-cut candidate projected onto it, so the
    /// Cut tool still works where no guide line reaches. Neither in range yields null.
    /// </summary>
    /// <param name="staticCandidates">Guide-intersection candidates from <see cref="FindCandidates"/>.</param>
    /// <param name="connections">Connections eligible for a free cut.</param>
    /// <param name="point">Pointer position in micrometers.</param>
    /// <param name="snapRadiusMicrometers">Snap/hit radius shared by both lookups.</param>
    /// <param name="requiredRunMicrometers">Straight-run guard, same as <see cref="FindCandidates"/>.</param>
    /// <param name="footprint">Grid clearance check; null skips it.</param>
    public ManualCrossingCandidate? ResolveCandidate(
        IReadOnlyList<ManualCrossingCandidate> staticCandidates,
        IEnumerable<WaveguideConnection> connections,
        (double X, double Y) point,
        double snapRadiusMicrometers,
        double requiredRunMicrometers,
        FootprintClearance? footprint = null)
    {
        var snapped = FindNearestCandidate(staticCandidates, point, snapRadiusMicrometers);
        if (snapped != null) return snapped;
        return FindNearestFreeCandidate(connections, point, snapRadiusMicrometers, requiredRunMicrometers, footprint);
    }

    /// <summary>
    /// Builds a free-cut candidate at the point on <paramref name="segment"/> nearest to
    /// <paramref name="nearPoint"/>. Rejects diagonal segments — the PDK crossing is strictly
    /// orthogonal — points too close to either end for the crossing to dock cleanly (the same
    /// straight-run guard <see cref="FindCandidates"/> applies to guide-based candidates), and
    /// points whose crossing footprint would overlap other geometry.
    /// </summary>
    /// <param name="footprint">Grid clearance check; null skips it.</param>
    public ManualCrossingCandidate? TryCreateFreeCandidate(
        WaveguideConnection connection, StraightSegment segment,
        (double X, double Y) nearPoint, double requiredRunMicrometers,
        FootprintClearance? footprint = null)
    {
        var direction = CrossingGeometry.GetDirection(segment);
        if (!CrossingGeometry.IsCardinalDirection(direction, AxisToleranceDegrees, out bool isHorizontal))
            return null;

        var point = ProjectOntoSegment(segment, nearPoint);
        if (!CrossingGeometry.HasStraightRunAround(segment, point, requiredRunMicrometers))
            return null;
        if (!IsFootprintClear(footprint, point, connection.Id))
            return null;

        return new ManualCrossingCandidate(connection, segment, GuideLine: null, point, isHorizontal, direction);
    }

    /// <summary>
    /// Finds the free-cut candidate nearest to <paramref name="point"/> across all eligible
    /// connections' straight segments, within <paramref name="maxDistanceMicrometers"/>. Only
    /// the geometrically nearest segment is tried — a rejected nearest segment (too little
    /// straight run, or a blocked footprint) yields no candidate rather than falling through
    /// to a farther one, so a click near a cramped spot does nothing instead of silently
    /// cutting somewhere else.
    /// </summary>
    /// <param name="footprint">Grid clearance check; null skips it.</param>
    public ManualCrossingCandidate? FindNearestFreeCandidate(
        IEnumerable<WaveguideConnection> connections,
        (double X, double Y) point,
        double maxDistanceMicrometers,
        double requiredRunMicrometers,
        FootprintClearance? footprint = null)
    {
        WaveguideConnection? nearestConnection = null;
        StraightSegment? nearestSegment = null;
        double bestDistance = maxDistanceMicrometers;

        foreach (var connection in connections)
        {
            if (connection.IsElectrical) continue;
            foreach (var segment in connection.GetPathSegments().OfType<StraightSegment>())
            {
                double distance = DistanceToProjection(segment, point);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                nearestSegment = segment;
                nearestConnection = connection;
            }
        }

        if (nearestSegment == null || nearestConnection == null) return null;
        return TryCreateFreeCandidate(nearestConnection, nearestSegment, point, requiredRunMicrometers, footprint);
    }

    /// <summary>Nearest candidate to <paramref name="point"/> within <paramref name="radiusMicrometers"/>, or null.</summary>
    private static ManualCrossingCandidate? FindNearestCandidate(
        IReadOnlyList<ManualCrossingCandidate> candidates, (double X, double Y) point, double radiusMicrometers)
    {
        ManualCrossingCandidate? best = null;
        double bestDistance = radiusMicrometers;
        foreach (var candidate in candidates)
        {
            double dx = candidate.IntersectionPoint.X - point.X;
            double dy = candidate.IntersectionPoint.Y - point.Y;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance <= bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }
        return best;
    }

    /// <summary>True when the crossing's bounding box at <paramref name="point"/> is free of
    /// other components/waveguides — the connection being cut is exempted since its own
    /// geometry occupies that exact spot already. Null footprint always passes.</summary>
    private static bool IsFootprintClear(FootprintClearance? footprint, (double X, double Y) point, Guid connectionId)
    {
        if (footprint is not { } clearance) return true;
        double half = clearance.HalfExtentMicrometers + clearance.Grid.ObstaclePaddingMicrometers;
        return clearance.Grid.IsAreaClearForCrossing(
            point.X - half, point.Y - half, point.X + half, point.Y + half,
            new HashSet<Guid> { connectionId });
    }

    /// <summary>Point on the segment (clamped to its endpoints) nearest to <paramref name="point"/>.</summary>
    private static (double X, double Y) ProjectOntoSegment(StraightSegment segment, (double X, double Y) point)
    {
        double dx = segment.EndPoint.X - segment.StartPoint.X;
        double dy = segment.EndPoint.Y - segment.StartPoint.Y;
        double lengthSquared = dx * dx + dy * dy;
        if (lengthSquared < 1e-9) return segment.StartPoint;

        double t = ((point.X - segment.StartPoint.X) * dx + (point.Y - segment.StartPoint.Y) * dy) / lengthSquared;
        t = Math.Clamp(t, 0.0, 1.0);
        return (segment.StartPoint.X + t * dx, segment.StartPoint.Y + t * dy);
    }

    private static double DistanceToProjection(StraightSegment segment, (double X, double Y) point)
    {
        var projected = ProjectOntoSegment(segment, point);
        double dx = point.X - projected.X;
        double dy = point.Y - projected.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Maps a pin's absolute facing angle to a cardinal unit direction, or null.</summary>
    private (double X, double Y)? DirectionForAngle(double angleDegrees)
    {
        double normalized = ((angleDegrees % 360.0) + 360.0) % 360.0;
        if (AngleNear(normalized, 0)) return (1, 0);
        if (AngleNear(normalized, 90)) return (0, 1);
        if (AngleNear(normalized, 180)) return (-1, 0);
        if (AngleNear(normalized, 270)) return (0, -1);
        return null;
    }

    private bool AngleNear(double normalizedDegrees, double target)
    {
        double diff = Math.Abs(normalizedDegrees - target) % 360.0;
        if (diff > 180.0) diff = 360.0 - diff;
        return diff <= AxisToleranceDegrees;
    }

    private static List<(PinGuideLine Guide, StraightSegment Ray)> BuildGuideRays(
        IReadOnlyList<PinGuideLine> guideLines)
    {
        var rays = new List<(PinGuideLine, StraightSegment)>(guideLines.Count);
        foreach (var guide in guideLines)
            rays.Add((guide, BuildRaySegment(guide)));
        return rays;
    }

    private static StraightSegment BuildRaySegment(PinGuideLine guide)
    {
        double angle = Math.Atan2(guide.Direction.Y, guide.Direction.X) * 180.0 / Math.PI;
        return new StraightSegment(
            guide.Origin.X, guide.Origin.Y,
            guide.Origin.X + guide.Direction.X * GuideRayLengthMicrometers,
            guide.Origin.Y + guide.Direction.Y * GuideRayLengthMicrometers,
            angle);
    }

    private static double DistanceFromOrigin(PinGuideLine guide, (double X, double Y) point)
    {
        double dx = point.X - guide.Origin.X;
        double dy = point.Y - guide.Origin.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// Buckets the point onto a grid sized by <see cref="DuplicateToleranceMicrometers"/> for an
    /// O(1) duplicate check (replacing an O(n) pairwise scan). Two points closer than the
    /// tolerance normally land in the same bucket; a point exactly on a bucket boundary may
    /// rarely be treated as distinct from an equally-close neighbour in an adjacent bucket —
    /// an acceptable approximation since duplicates only ever arise from the same physical
    /// intersection computed via two numerically-near paths.
    /// </summary>
    private static bool TryMarkSeen(HashSet<(long, long)> seenBuckets, (double X, double Y) point)
    {
        var bucket = (
            (long)Math.Round(point.X / DuplicateToleranceMicrometers),
            (long)Math.Round(point.Y / DuplicateToleranceMicrometers));
        return seenBuckets.Add(bucket);
    }
}
