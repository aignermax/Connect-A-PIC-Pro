using CAP_Core.Components.Core;
using CAP_Core.Routing.AStarPathfinder;

namespace CAP_Core.Routing;

/// <summary>
/// Direct/S-bend-first policy of <see cref="WaveguideRouter"/> (issues #860, #874):
/// building the styled candidate and verifying it before A* runs. Component bodies are
/// checked against the obstacle grid (with the A* pin corridors cleared); sibling
/// waveguides are checked GEOMETRICALLY against their exact registered segments. The
/// rasterized waveguide cells over-approximate each sibling by up to a grid cell plus
/// half the obstacle width (~6 µm at defaults), which walled off dense fan-out arrays:
/// once the first routes were registered, every later styled candidate read as blocked
/// even though it merely ran parallel to a neighbor (field report: only ~25 % of a
/// 166-connection import routed cleanly, the rest degraded to red blocked fallbacks).
/// </summary>
public partial class WaveguideRouter
{
    /// <summary>Pin corridor length as a multiple of the bend radius — the same corridor
    /// <see cref="TryRouteAStar"/> clears. Also used as the endpoint exemption radius of
    /// the sibling clearance check: within it, proximity to fan-out siblings attached to
    /// neighboring pins is dictated by the fixed pin pitch, not by the route.</summary>
    private const double CorridorLengthRadiusFactor = 3.0;

    /// <summary>Endpoint match tolerance (µm) used to recognize a registered obstacle as
    /// this connection's own stale route (same pin pair) so it never blocks its re-route.</summary>
    private const double SameEndpointToleranceMicrometers = 1.0;

    /// <summary>
    /// Direct/S-bend-first policy (issue #860): builds the styled candidate for the pin
    /// geometry and accepts it only when it is clean, clear of component bodies on the
    /// grid A* uses (with the same pin corridors cleared), and geometrically compatible
    /// with every registered sibling route — no crossing, and no closer than
    /// <see cref="MinWaveguideSpacingMicrometers"/> outside the pin fan-out zones.
    /// Returns null when no styled geometry fits or the candidate conflicts — A* then
    /// routes as before.
    /// </summary>
    private RoutedPath? TryRouteDirect(PhysicalPin startPin, PhysicalPin endPin, double bendRadius)
    {
        var candidate = InterconnectRouting.DirectRouteFirstPolicy.TryBuildWithStyle(
            startPin, endPin, bendRadius, out var directStyle);
        if (candidate == null
            || !candidate.IsValid
            || PathIntersectionDetector.HasSelfIntersection(candidate)
            || IsDirectCandidateBlockedByComponents(candidate.Segments, startPin, endPin, bendRadius)
            || DirectCandidateConflictsWithSibling(candidate, startPin, endPin, bendRadius))
        {
            return null;
        }

        candidate.IsDirectStyledRoute = true;
        candidate.DirectStyle = directStyle;
        return candidate;
    }

    /// <summary>
    /// Component blocked-cell test for the direct styled candidate, on the SAME grid state
    /// A* would route on: the pin corridors <see cref="TryRouteAStar"/> clears (start
    /// outward, end facing and end terminal — 3·radius long, radius wide) are cleared
    /// for the test and restored afterwards. A styled path may therefore dip into the
    /// endpoint components' own cells at the pin exit/entry — but a path that keeps
    /// running THROUGH a component body beyond the corridor (field report: the S-bend
    /// flowed straight through the target component whose pin faced away) stays
    /// blocked and defers to A*, which routes around the body. Sibling waveguides are
    /// deliberately NOT judged by cells here — their exact geometry is checked in
    /// <see cref="DirectCandidateConflictsWithSibling"/> instead.
    /// </summary>
    private bool IsDirectCandidateBlockedByComponents(
        IReadOnlyList<PathSegment> segments, PhysicalPin startPin, PhysicalPin endPin, double bendRadius)
    {
        if (PathfindingGrid == null) return false;

        var (startX, startY) = startPin.GetAbsolutePosition();
        var (endX, endY) = endPin.GetAbsolutePosition();
        double startAngle = startPin.GetAbsoluteAngle();
        double endFacingAngle = endPin.GetAbsoluteAngle();
        double endInputAngle = AngleUtilities.NormalizeAngle(endFacingAngle + 180);
        double corridorLength = bendRadius * CorridorLengthRadiusFactor;
        double corridorWidth = bendRadius;

        var clearedStart = PathfindingGrid.ClearPinCorridor(
            startX, startY, startAngle, corridorLength, corridorWidth);
        var clearedEndApproach = PathfindingGrid.ClearPinCorridor(
            endX, endY, endFacingAngle, corridorLength, corridorWidth);
        var clearedEndTerminal = PathfindingGrid.ClearPinCorridor(
            endX, endY, endInputAngle, corridorLength, corridorWidth);
        try
        {
            return IsPathBlocked(segments, PathfindingGrid.IsBlockedByComponent);
        }
        finally
        {
            PathfindingGrid.RestoreCells(clearedStart);
            PathfindingGrid.RestoreCells(clearedEndApproach);
            PathfindingGrid.RestoreCells(clearedEndTerminal);
        }
    }

    /// <summary>
    /// Exact geometric verdict against every registered sibling route (issue #874): the
    /// candidate is rejected when it properly CROSSES a sibling, or when it comes closer
    /// than <see cref="MinWaveguideSpacingMicrometers"/> outside the pin fan-out zones
    /// (within 3·radius of the candidate's own pins, proximity to neighbors is dictated
    /// by the fixed pin pitch and tolerated — matching the A* pin-corridor allowance).
    /// A registered obstacle with this connection's own endpoints is its stale previous
    /// route and is skipped.
    /// </summary>
    private bool DirectCandidateConflictsWithSibling(
        RoutedPath candidate, PhysicalPin startPin, PhysicalPin endPin, double bendRadius)
    {
        if (PathfindingGrid == null) return false;
        var siblings = PathfindingGrid.GetWaveguideGeometries();
        if (siblings.Count == 0) return false;

        var (startX, startY) = startPin.GetAbsolutePosition();
        var (endX, endY) = endPin.GetAbsolutePosition();
        double exemptionRadius = bendRadius * CorridorLengthRadiusFactor;

        foreach (var segments in siblings)
        {
            if (segments.Count == 0 || IsOwnStaleRoute(segments, startX, startY, endX, endY))
                continue;

            var sibling = new RoutedPath();
            sibling.Segments.AddRange(segments);
            if (PathIntersectionDetector.Crosses(candidate, sibling))
                return true;
            if (PathIntersectionDetector.ComesCloserThan(
                    candidate, sibling, MinWaveguideSpacingMicrometers, exemptionRadius))
                return true;
        }
        return false;
    }

    /// <summary>True when the registered segments run between the same two pin positions
    /// (either orientation) — i.e. they are this connection's own previous route.</summary>
    private static bool IsOwnStaleRoute(
        IReadOnlyList<PathSegment> segments,
        double startX, double startY, double endX, double endY)
    {
        var first = segments[0].StartPoint;
        var last = segments[^1].EndPoint;
        return (IsSamePoint(first.X, first.Y, startX, startY) && IsSamePoint(last.X, last.Y, endX, endY))
            || (IsSamePoint(first.X, first.Y, endX, endY) && IsSamePoint(last.X, last.Y, startX, startY));
    }

    /// <summary>True when two points coincide within <see cref="SameEndpointToleranceMicrometers"/>.</summary>
    private static bool IsSamePoint(double x1, double y1, double x2, double y2)
    {
        double dx = x2 - x1;
        double dy = y2 - y1;
        return dx * dx + dy * dy
            <= SameEndpointToleranceMicrometers * SameEndpointToleranceMicrometers;
    }
}
