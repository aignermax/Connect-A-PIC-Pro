using CAP_Core.Routing;
using CAP_Core.Routing.AStarPathfinder;

namespace CAP_Core.Components.Connections;

/// <summary>
/// Post-routing pass that grows the bend radii of freshly auto-routed optical connections
/// to the largest allowed value the free space permits (<see cref="BendRadiusUpsizer"/>).
/// It runs once, after every route is final and registered, so each candidate arc is vetoed
/// against ALL sibling routes — the reason it lives here and not inside a single-route
/// <see cref="WaveguideRouter"/> attempt, which cannot see routes computed later and would
/// let early routes claim space later routes need. Electrical (metal) routes are excluded:
/// their radius is governed by the RF process floor, and trace geometry must stay put so
/// GDS exports of mixed designs remain stable.
/// </summary>
public partial class WaveguideConnectionManager
{
    /// <summary>
    /// Upsizes bend radii of every auto-routed, non-frozen, cleanly routed optical
    /// connection. Each upsize is computed on a private copy with the route's own grid
    /// obstacle removed and, only if accepted, published through an atomic reference swap;
    /// the grid obstacle is re-registered either way. Routes that already touch or cross a
    /// sibling are conservatively left untouched, and an accepted upsize must not bring the
    /// route closer to any nearby sibling than it was before (same contract as the pin-lead
    /// collapse pass).
    /// </summary>
    private void UpsizeAutoRouteBendRadii(CancellationToken cancellationToken)
    {
        var grid = _router.PathfindingGrid;
        if (grid == null || _router.AllowedBendRadii.Count == 0)
            return;

        var upsizer = new BendRadiusUpsizer(grid, _router.AllowedBendRadii);
        var connections = SnapshotConnections();
        var boxCache = new Dictionary<RoutedPath, BoundingBox>();

        foreach (var connection in connections)
        {
            if (cancellationToken.IsCancellationRequested)
                return;
            if (!IsBendUpsizable(connection))
                continue;

            var original = connection.RoutedPath!;
            if (!TryCollectSiblingGuards(connection, connections, original, boxCache,
                    out var nearSiblings, out var clearanceBefore, out var degenerateGuards))
                continue; // already touches/crosses a neighbour — conservatively leave it be

            var working = original.DeepCopy();
            grid.RemoveWaveguideObstacle(connection.Id);

            if (upsizer.TryUpsize(working) &&
                IsUpsizeAcceptable(working, nearSiblings, clearanceBefore, degenerateGuards))
            {
                connection.ReplaceRoutedPath(working);
            }

            grid.AddWaveguideObstacle(connection.Id,
                connection.RoutedPath!.Segments, WaveguideWidthMicrometers);
        }
    }

    /// <summary>
    /// Accepts an upsized path only when it stays simple, comes no closer to any nearby
    /// sibling than the pre-upsize geometry did (no-worsening, capped at the waveguide
    /// spacing), and keeps every degenerate neighbour at its required clearance.
    /// </summary>
    private bool IsUpsizeAcceptable(RoutedPath working,
        IReadOnlyList<RoutedPath> nearSiblings, IReadOnlyList<double> clearanceBefore,
        IReadOnlyList<DegenerateSiblingGuard> degenerateGuards)
    {
        if (PathIntersectionDetector.HasSelfIntersection(working))
            return false;

        for (int i = 0; i < nearSiblings.Count; i++)
        {
            double required = Math.Min(
                _router.MinWaveguideSpacingMicrometers, clearanceBefore[i]);
            if (PathIntersectionDetector.MinimumDistance(working, nearSiblings[i])
                < required - SiblingTouchToleranceMicrometers)
                return false;
        }
        foreach (var guard in degenerateGuards)
        {
            if (DistanceToDegenerate(working, guard.Start, guard.End)
                < guard.RequiredClearance - SiblingTouchToleranceMicrometers)
                return false;
        }
        return true;
    }

    /// <summary>True for a route the upsizing pass may edit: an optical auto route with a
    /// clean, unfrozen, non-fallback path. Direct styled routes (issue #860) keep their
    /// intended stub geometry; electrical routes keep their RF-floored metal bends.</summary>
    private static bool IsBendUpsizable(WaveguideConnection connection) =>
        connection.Type == WaveguideType.Auto
        && !connection.IsRouteFrozen
        && !connection.IsElectrical
        && connection.RoutedPath is { IsBlockedFallback: false, IsDirectStyledRoute: false } path
        && path.IsValid
        && path.Segments.Count > 0;
}
