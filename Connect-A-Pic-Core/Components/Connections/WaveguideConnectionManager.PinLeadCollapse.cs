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
/// attempt, which cannot see routes computed later. The acceptance introduces NO new validation
/// semantics: a trial must pass the UNCHANGED component check
/// (<see cref="WaveguideRouter.IsPathBlockedByComponents"/> — the same verdict incremental route
/// validation applies once waveguide obstacles are cleared), so an accepted collapse is by
/// construction a route the next pass keeps. Where the check rejects, the lead collapses
/// partially to the largest accepted shift or not at all — a residual lead is a correct result;
/// the manual segment-shift handle remains the escape hatch.
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

    /// <summary>Total path length (µm) below which a route samples to fewer than two points and
    /// polyline-to-polyline distance is meaningless; such siblings are measured as a point.</summary>
    private const double DegenerateSiblingLengthMicrometers = 0.1;

    private readonly record struct BoundingBox(double MinX, double MinY, double MaxX, double MaxY);

    /// <summary>Pre-collapse guard for a degenerate (point-like) sibling: the trial's distance
    /// to it must not fall below <see cref="RequiredClearance"/>. Both polyline endpoints are
    /// measured — a degenerate can still be up to the degenerate threshold long, and either
    /// end may be the near one.</summary>
    private readonly record struct DegenerateSiblingGuard(
        (double X, double Y) Start, (double X, double Y) End, double RequiredClearance);

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
            if (!TryCollectSiblingGuards(connection, connections, original, boxCache,
                    out var nearSiblings, out var clearanceBefore, out var degenerateGuards))
                continue; // already touches/crosses a neighbour — conservatively leave it be

            var working = original.DeepCopy();
            PinStraightCollapser.Collapse(working,
                trial => IsCollapseAcceptable(trial, nearSiblings, clearanceBefore, degenerateGuards));

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
    /// affect their clearance, with each one's pre-collapse distance. Blocked-fallback siblings
    /// count — they are rendered and registered obstacles. Returns false when the route already
    /// touches or crosses a neighbour: such routes are conservatively never collapsed (a collapse
    /// must never add or worsen a crossing). Degenerate neighbours do not veto; they are measured
    /// point-to-polyline and guarded separately.
    /// </summary>
    private bool TryCollectSiblingGuards(WaveguideConnection connection,
        IReadOnlyList<WaveguideConnection> connections, RoutedPath original,
        Dictionary<RoutedPath, BoundingBox> boxCache,
        out List<RoutedPath> nearSiblings, out List<double> clearanceBefore,
        out List<DegenerateSiblingGuard> degenerateGuards)
    {
        nearSiblings = new List<RoutedPath>();
        clearanceBefore = new List<double>();
        degenerateGuards = new List<DegenerateSiblingGuard>();

        double reach = PinStraightCollapser.TotalPinLead(original) * DiagonalShiftFactor
            + _router.MinWaveguideSpacingMicrometers + CollapseSearchMarginMicrometers;
        var box = BoxFor(original, boxCache);

        foreach (var other in connections)
        {
            if (other == connection || other.RoutedPath is not { } sibling || !other.IsPathValid)
                continue;
            if (BoxGap(box, BoxFor(sibling, boxCache)) > reach)
                continue; // too far for this collapse to move within touching range

            if (sibling.TotalLengthMicrometers < DegenerateSiblingLengthMicrometers)
            {
                var start = sibling.Segments[0].StartPoint;
                var end = sibling.Segments[^1].EndPoint;
                double before = DistanceToDegenerate(original, start, end);
                // No-worsening, consistent with real siblings: a route already inside the
                // spacing only must not get closer; one outside must not drop below it.
                degenerateGuards.Add(new DegenerateSiblingGuard(start, end,
                    Math.Min(_router.MinWaveguideSpacingMicrometers, before)));
                continue;
            }

            double distance = PathIntersectionDetector.MinimumDistance(original, sibling);
            if (distance < SiblingTouchToleranceMicrometers)
                return false;
            nearSiblings.Add(sibling);
            clearanceBefore.Add(distance);
        }
        return true;
    }

    /// <summary>
    /// Accepts a collapse trial only when it stays simple, passes the unchanged component check
    /// (the same verdict incremental route validation applies once waveguide obstacles are
    /// cleared), comes no closer to any nearby sibling than the pre-collapse geometry did, and
    /// keeps every degenerate neighbour at least the waveguide spacing (or its previous
    /// distance, whichever is smaller — no-worsening) away.
    /// </summary>
    private bool IsCollapseAcceptable(RoutedPath trial,
        IReadOnlyList<RoutedPath> nearSiblings, IReadOnlyList<double> clearanceBefore,
        IReadOnlyList<DegenerateSiblingGuard> degenerateGuards)
    {
        if (PathIntersectionDetector.HasSelfIntersection(trial))
            return false;
        if (_router.IsPathBlockedByComponents(trial.Segments))
            return false;

        for (int i = 0; i < nearSiblings.Count; i++)
        {
            if (PathIntersectionDetector.MinimumDistance(trial, nearSiblings[i])
                < clearanceBefore[i] - SiblingTouchToleranceMicrometers)
                return false;
        }
        foreach (var guard in degenerateGuards)
        {
            if (DistanceToDegenerate(trial, guard.Start, guard.End)
                < guard.RequiredClearance - SiblingTouchToleranceMicrometers)
                return false;
        }
        return true;
    }

    /// <summary>Distance from a path to a degenerate sibling, measured against both of the
    /// sibling's polyline endpoints.</summary>
    private static double DistanceToDegenerate(
        RoutedPath path, (double X, double Y) start, (double X, double Y) end)
        => Math.Min(
            PathIntersectionDetector.DistanceToPoint(path, start.X, start.Y),
            PathIntersectionDetector.DistanceToPoint(path, end.X, end.Y));

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
