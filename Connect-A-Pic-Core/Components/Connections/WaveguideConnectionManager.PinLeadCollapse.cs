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
/// attempt, which cannot see routes computed later. Every trial is checked with the shared,
/// read-only own-pin predicate (<see cref="WaveguideRouter.IsPathBlockedByComponentsForConnection"/>)
/// that also decides route validation, unfreezing and shift collision — so an accepted collapse
/// is by construction a route the next pass keeps. A rejected trial simply leaves a residual
/// lead, which is correct.
/// </summary>
public partial class WaveguideConnectionManager
{
    /// <summary>Distance (µm) below which two routes count as touching/crossing.</summary>
    private const double SiblingTouchToleranceMicrometers = 1e-3;

    /// <summary>Extra reach (µm) beyond the collapse's maximum movement when selecting affected
    /// siblings. At least the waveguide spacing, so a sibling that could be pushed to the spacing
    /// limit is still evaluated.</summary>
    private const double CollapseSearchMarginMicrometers = 2.0;

    /// <summary>A shift moves geometry by |delta|/|dot|; a 45° diagonal outer straight makes that
    /// √2 × the collapsed lead, so the worst-case point movement is scaled by this factor.</summary>
    private const double DiagonalShiftFactor = 1.41421356237; // √2

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
        var boxCache = new Dictionary<RoutedPath, BoundingBox>();

        foreach (var connection in connections)
        {
            if (cancellationToken.IsCancellationRequested)
                return;
            if (!IsPinLeadCollapsible(connection) ||
                !PinStraightCollapser.HasCollapsibleLead(connection.RoutedPath!))
                continue;

            var original = connection.RoutedPath!;
            var siblingConstraints =
                CollectSiblingConstraints(connection, connections, original, boxCache);

            double radius = connection.RoutedBendRadiusMicrometers(
                _router.ProcessMinBendRadiusMicrometers);
            var working = original.DeepCopy();
            PinStraightCollapser.Collapse(working,
                trial => IsCollapseAcceptable(trial, connection, radius, siblingConstraints));

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
    /// Captures a <see cref="CollapseSiblingConstraint"/> for every sibling close enough that a
    /// collapse of <paramref name="original"/> could affect it. Blocked-fallback siblings count —
    /// they are rendered and registered obstacles. Nothing is a blanket veto here: degenerate
    /// and already-touching neighbours get their own criteria inside the constraint.
    /// </summary>
    private List<CollapseSiblingConstraint> CollectSiblingConstraints(
        WaveguideConnection connection, IReadOnlyList<WaveguideConnection> connections,
        RoutedPath original, Dictionary<RoutedPath, BoundingBox> boxCache)
    {
        var constraints = new List<CollapseSiblingConstraint>();
        double reach = LeadSum(original) * DiagonalShiftFactor
            + _router.MinWaveguideSpacingMicrometers + CollapseSearchMarginMicrometers;
        var box = BoxFor(original, boxCache);

        foreach (var other in connections)
        {
            if (other == connection || other.RoutedPath is not { } sibling || !other.IsPathValid)
                continue;
            if (BoxGap(box, BoxFor(sibling, boxCache)) > reach)
                continue; // too far for this collapse to move within touching range

            constraints.Add(CollapseSiblingConstraint.For(original, sibling,
                _router.MinWaveguideSpacingMicrometers, SiblingTouchToleranceMicrometers));
        }
        return constraints;
    }

    /// <summary>
    /// Accepts a collapse trial only when it stays simple, passes the shared own-pin component
    /// predicate (padding band at the own pins tolerated, bodies and frozen group cells never),
    /// and does not worsen any nearby sibling's situation.
    /// </summary>
    private bool IsCollapseAcceptable(RoutedPath trial, WaveguideConnection connection,
        double radius, IReadOnlyList<CollapseSiblingConstraint> siblingConstraints)
    {
        if (PathIntersectionDetector.HasSelfIntersection(trial))
            return false;
        if (_router.IsPathBlockedByComponentsForConnection(
                trial.Segments, connection.StartPin, connection.EndPin, radius))
            return false;
        return siblingConstraints.All(constraint => constraint.IsSatisfiedBy(trial));
    }

    /// <summary>Combined length of the two pin-side straight leads (0 for a lead that is a bend).</summary>
    private static double LeadSum(RoutedPath path)
    {
        double sum = 0;
        if (path.Segments[0] is StraightSegment first)
            sum += first.LengthMicrometers;
        if (path.Segments[^1] is StraightSegment last)
            sum += last.LengthMicrometers;
        return sum;
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

    private static BoundingBox BoxFor(RoutedPath path, Dictionary<RoutedPath, BoundingBox> cache)
    {
        if (!cache.TryGetValue(path, out var box))
        {
            box = BoundingBoxOf(path);
            cache[path] = box;
        }
        return box;
    }

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
