using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Components.PinKinds;

namespace CAP_Core.Routing.CrossingInsertion.ManualCrossing;

/// <summary>
/// Computes Cut-tool guide lines and crossing-insertion candidates (issue #798).
/// Guide lines are rays from axis-aligned optical pins; candidates are the
/// interior intersections of those rays with perpendicular straight waveguide
/// segments that leave enough straight run to dock a crossing component.
/// Manhattan-only in v1: arcs and diagonal segments are skipped.
/// </summary>
public class ManualCrossingCandidateFinder
{
    /// <summary>Length used to extend a guide ray for intersection math (µm).</summary>
    private const double GuideRayLengthMicrometers = 1_000_000.0;

    /// <summary>Candidates closer together than this are duplicates of the same spot (µm).</summary>
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
    /// of straight run on both sides of the crossing center.
    /// </summary>
    /// <param name="guideLines">Guide lines from <see cref="BuildGuideLines"/>.</param>
    /// <param name="connections">Connections whose routed paths are intersected.</param>
    /// <param name="requiredStraightRunMicrometers">
    /// Crossing half-extent plus stub clearance — the straight run each side of
    /// the intersection must offer so the crossing ports dock cleanly.
    /// </param>
    public IReadOnlyList<ManualCrossingCandidate> FindCandidates(
        IReadOnlyList<PinGuideLine> guideLines,
        IEnumerable<WaveguideConnection> connections,
        double requiredStraightRunMicrometers)
    {
        var candidates = new List<ManualCrossingCandidate>();
        foreach (var connection in connections)
        {
            if (connection.IsElectrical) continue;
            foreach (var segment in connection.GetPathSegments().OfType<StraightSegment>())
                CollectSegmentCandidates(candidates, connection, segment,
                    guideLines, requiredStraightRunMicrometers);
        }
        return candidates;
    }

    private void CollectSegmentCandidates(
        List<ManualCrossingCandidate> candidates,
        WaveguideConnection connection,
        StraightSegment segment,
        IReadOnlyList<PinGuideLine> guideLines,
        double requiredRunMicrometers)
    {
        var segmentDirection = CrossingGeometry.GetDirection(segment);
        foreach (var guide in guideLines)
        {
            if (guide.Pin.ParentComponent != null &&
                (connection.StartPin == guide.Pin || connection.EndPin == guide.Pin))
                continue;

            if (!CrossingGeometry.IsAxisAlignedRightAngle(
                    guide.Direction, segmentDirection, AxisToleranceDegrees, out bool guideIsHorizontal))
                continue;

            var ray = BuildRaySegment(guide);
            if (!CrossingGeometry.TryGetIntersection(ray, segment, out var point)) continue;
            if (DistanceFromOrigin(guide, point) < requiredRunMicrometers) continue;
            if (!CrossingGeometry.HasStraightRunAround(segment, point, requiredRunMicrometers)) continue;
            if (IsDuplicate(candidates, point)) continue;

            candidates.Add(new ManualCrossingCandidate(
                connection, segment, guide, point, !guideIsHorizontal, segmentDirection));
        }
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

    private static bool IsDuplicate(
        List<ManualCrossingCandidate> candidates, (double X, double Y) point)
    {
        return candidates.Any(existing =>
            Math.Abs(existing.IntersectionPoint.X - point.X) < DuplicateToleranceMicrometers &&
            Math.Abs(existing.IntersectionPoint.Y - point.Y) < DuplicateToleranceMicrometers);
    }
}
