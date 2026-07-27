using CAP_Core.Routing;
using CAP_Core.Routing.InterconnectRouting.SegmentShift;

namespace CAP_Core.Components.Connections;

/// <summary>
/// Post-routing pass that pulls the pin-side straight leads of freshly auto-routed connections
/// onto their pins (<see cref="PinStraightCollapser"/>). The grid escape and cell quantization
/// leave a short forced straight between a pin and the first/last bend; without this pass the
/// user has to shift it away by hand after every re-route. It runs once, after every route is
/// final and registered, so each collapse is validated against ALL sibling routes at their final
/// positions — the reason it lives here and not inside a single-route <see cref="WaveguideRouter"/>
/// attempt, which cannot see routes computed later.
/// </summary>
public partial class WaveguideConnectionManager
{
    /// <summary>Distance (µm) below which two routes count as touching/crossing.</summary>
    private const double SiblingTouchToleranceMicrometers = 1e-3;

    /// <summary>Extra reach (µm) added to a collapse's maximum point movement when deciding which
    /// siblings are close enough to be affected.</summary>
    private const double CollapseSearchMarginMicrometers = 1.0;

    private readonly record struct BoundingBox(double MinX, double MinY, double MaxX, double MaxY);

    /// <summary>
    /// Collapses the pin leads of every auto-routed, non-frozen, cleanly routed connection.
    /// Frozen or manually edited routes and blocked fallbacks are left untouched. Each collapse
    /// is computed on a private copy and, only if accepted, published through an atomic reference
    /// swap so the UI thread never sees half-mutated live geometry.
    /// </summary>
    private void CollapseAutoRoutePinLeads(CancellationToken cancellationToken)
    {
        var grid = _router.PathfindingGrid;
        if (grid == null)
            return;

        var connections = SnapshotConnections();
        foreach (var connection in connections)
        {
            if (cancellationToken.IsCancellationRequested)
                return;
            if (!IsPinLeadCollapsible(connection) ||
                !PinStraightCollapser.HasCollapsibleLead(connection.RoutedPath!))
                continue;

            var original = connection.RoutedPath!;
            if (!TryCollectNearSiblings(connection, connections, original,
                    out var nearSiblings, out var clearanceBefore))
                continue; // already touches a neighbour — leave it to the crossing resolver

            var working = original.DeepCopy();
            PinStraightCollapser.Collapse(working,
                trial => IsCollapseAcceptable(trial, connection, nearSiblings, clearanceBefore));

            if (PathsEquivalent(working, original))
                continue;

            connection.ReplaceRoutedPath(working);
            grid.AddWaveguideObstacle(connection.Id, working.Segments, WaveguideWidthMicrometers);
        }
    }

    /// <summary>True for a route the collapse pass may edit: an auto route with a clean,
    /// unfrozen, non-fallback path.</summary>
    private static bool IsPinLeadCollapsible(WaveguideConnection connection) =>
        connection.Type == WaveguideType.Auto
        && !connection.IsRouteFrozen
        && connection.RoutedPath is { IsBlockedFallback: false } path
        && path.IsValid
        && path.Segments.Count > 0;

    /// <summary>
    /// Gathers the siblings close enough that a collapse of <paramref name="original"/> could
    /// affect their clearance, with each one's pre-collapse distance. Returns false when the
    /// route already touches or crosses a neighbour, so the caller skips it (a collapse must
    /// never add a second crossing on top of the crossing resolver's decisions).
    /// </summary>
    private bool TryCollectNearSiblings(WaveguideConnection connection,
        IReadOnlyList<WaveguideConnection> connections, RoutedPath original,
        out List<RoutedPath> nearSiblings, out List<double> clearanceBefore)
    {
        nearSiblings = new List<RoutedPath>();
        clearanceBefore = new List<double>();

        double reach = MaxLeadMovement(original) + CollapseSearchMarginMicrometers;
        var box = BoundingBoxOf(original);
        foreach (var other in connections)
        {
            if (other == connection ||
                other.RoutedPath is not { IsBlockedFallback: false } sibling ||
                !other.IsPathValid)
                continue;
            if (BoxGap(box, BoundingBoxOf(sibling)) > reach)
                continue; // too far for this collapse to move within touching range

            double distance = PathIntersectionDetector.MinimumDistance(original, sibling);
            if (distance < SiblingTouchToleranceMicrometers)
                return false;
            nearSiblings.Add(sibling);
            clearanceBefore.Add(distance);
        }
        return true;
    }

    /// <summary>
    /// Accepts a collapse trial only when it stays simple, clears every foreign component body
    /// (exactly the verdict the incremental router applies when keeping a route), and comes no
    /// closer to any nearby sibling than the pre-collapse geometry did.
    /// </summary>
    private bool IsCollapseAcceptable(RoutedPath trial, WaveguideConnection connection,
        IReadOnlyList<RoutedPath> nearSiblings, IReadOnlyList<double> clearanceBefore)
    {
        if (PathIntersectionDetector.HasSelfIntersection(trial))
            return false;
        if (_router.IsPathBlockedByForeignComponents(trial.Segments, connection.StartPin, connection.EndPin))
            return false;

        for (int i = 0; i < nearSiblings.Count; i++)
        {
            if (PathIntersectionDetector.MinimumDistance(trial, nearSiblings[i])
                < clearanceBefore[i] - SiblingTouchToleranceMicrometers)
                return false;
        }
        return true;
    }

    /// <summary>Largest distance any point can move when both pin leads collapse: the sum of the
    /// two pin-side straight lengths (a parallel shift moves geometry by the collapsed length).</summary>
    private static double MaxLeadMovement(RoutedPath path)
    {
        double movement = 0;
        if (path.Segments[0] is StraightSegment first)
            movement += first.LengthMicrometers;
        if (path.Segments[^1] is StraightSegment last)
            movement += last.LengthMicrometers;
        return movement;
    }

    /// <summary>True when the two paths have the same segment count and matching endpoints.</summary>
    private static bool PathsEquivalent(RoutedPath a, RoutedPath b)
    {
        if (a.Segments.Count != b.Segments.Count)
            return false;
        for (int i = 0; i < a.Segments.Count; i++)
        {
            if (!PointsEqual(a.Segments[i].StartPoint, b.Segments[i].StartPoint) ||
                !PointsEqual(a.Segments[i].EndPoint, b.Segments[i].EndPoint))
                return false;
        }
        return true;
    }

    private static bool PointsEqual((double X, double Y) a, (double X, double Y) b) =>
        Math.Abs(a.X - b.X) < SiblingTouchToleranceMicrometers &&
        Math.Abs(a.Y - b.Y) < SiblingTouchToleranceMicrometers;

    /// <summary>Axis-aligned bounding box of a path (bends bounded conservatively by centre ± radius).</summary>
    private static BoundingBox BoundingBoxOf(RoutedPath path)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        void Include(double x, double y)
        {
            minX = Math.Min(minX, x); minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
        }

        foreach (var segment in path.Segments)
        {
            Include(segment.StartPoint.X, segment.StartPoint.Y);
            Include(segment.EndPoint.X, segment.EndPoint.Y);
            if (segment is BendSegment bend)
            {
                Include(bend.Center.X - bend.RadiusMicrometers, bend.Center.Y - bend.RadiusMicrometers);
                Include(bend.Center.X + bend.RadiusMicrometers, bend.Center.Y + bend.RadiusMicrometers);
            }
        }
        return new BoundingBox(minX, minY, maxX, maxY);
    }

    /// <summary>Gap between two boxes (0 when they overlap) — a lower bound on the path distance.</summary>
    private static double BoxGap(BoundingBox a, BoundingBox b)
    {
        double dx = Math.Max(0, Math.Max(a.MinX - b.MaxX, b.MinX - a.MaxX));
        double dy = Math.Max(0, Math.Max(a.MinY - b.MaxY, b.MinY - a.MaxY));
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
