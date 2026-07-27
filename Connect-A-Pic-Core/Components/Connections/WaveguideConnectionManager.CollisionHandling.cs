using CAP_Core.Routing;

namespace CAP_Core.Components.Connections;

/// <summary>
/// Collision handling for frozen and styled routes: unfreezing manually edited AUTO routes
/// that a component now overlaps, flagging styled routes that pass through components, and
/// marking unavoidable sibling crossings as blocked instead of leaving them silent.
/// </summary>
public partial class WaveguideConnectionManager
{
    /// <summary>
    /// Detects a component collision on a FROZEN AUTO route (manual bend edit) and, when
    /// found, treats it like an endpoint move: the route is unfrozen and the manual bend
    /// overrides are discarded so the connection is re-routed around the component.
    /// Uses the true arc geometry against component obstacles only, so the verdict does
    /// not depend on which sibling waveguides are currently registered in the grid — and
    /// via the shared own-pin predicate, so a bend hugging its OWN pin (a collapsed lead)
    /// never counts as a collision: unfreezing on it would let the next recalculation
    /// silently destroy the user's manual edits.
    /// Styled routes (Type != Auto) are never unfrozen here — their shape is forced and a
    /// collision is surfaced via <see cref="RoutedPath.PassesThroughComponent"/> instead.
    /// </summary>
    /// <returns>True when the connection was unfrozen and must be re-routed.</returns>
    private static bool TryUnfreezeCollidedAutoRoute(WaveguideConnection connection, WaveguideRouter router)
    {
        if (connection.Type != WaveguideType.Auto || !connection.IsRouteFrozen)
            return false;
        if (!connection.FrozenPathStillMatchesPins())
            return false; // Endpoint moved: RecalculateTransmission already unfreezes.
        if (!router.IsPathBlockedByComponentsForConnection(
                connection.RoutedPath!.Segments, connection.StartPin, connection.EndPin,
                connection.RoutedBendRadiusMicrometers(router.ProcessMinBendRadiusMicrometers)))
            return false;

        connection.IsRouteFrozen = false;
        connection.BendRadiusOverrides.Clear();
        connection.StraightShiftOffsets.Clear();
        return true;
    }

    /// <summary>
    /// Refreshes <see cref="RoutedPath.PassesThroughComponent"/> on a STYLED route.
    /// Styled routes ignore obstacles by design, so a component dropped onto the curve
    /// never triggers a re-route; the flag makes the design checks report the collision.
    /// No-op for Auto connections and connections without a routed path.
    /// </summary>
    private static void RefreshStyledObstacleCollision(WaveguideConnection connection, WaveguideRouter router)
    {
        if (connection.Type == WaveguideType.Auto || connection.RoutedPath == null)
            return;
        connection.RoutedPath.PassesThroughComponent =
            router.IsPathBlockedByComponents(connection.RoutedPath.Segments);
    }

    /// <summary>
    /// Marks routes that still properly cross a sibling after all routing passes (including
    /// the ordering cascade and crossing insertion) as blocked fallbacks — the existing
    /// controlled degradation — instead of leaving a silent crossing on the canvas.
    /// Only the re-routable side (Auto, not frozen) is flagged: it is re-routed on the next
    /// pass and rendered as blocked until the crossing is resolved. Forced routes (styled or
    /// manually frozen) keep their shape; their overlap is reported by the design checks.
    /// </summary>
    private void MarkUnresolvedSiblingCrossings()
    {
        // Snapshot: runs at the end of the routing pass on the routing thread while
        // UI commands may mutate the list — see _connectionsSync in the main partial.
        var routed = SnapshotConnections()
            .Where(c => c.RoutedPath != null && c.IsPathValid)
            .ToList();

        for (int i = 0; i < routed.Count; i++)
        {
            for (int j = i + 1; j < routed.Count; j++)
            {
                if (!PathIntersectionDetector.Crosses(routed[i].RoutedPath!, routed[j].RoutedPath!))
                    continue;
                var target = PickReroutableSide(routed[i], routed[j]);
                if (target != null)
                    target.RoutedPath!.IsBlockedFallback = true;
            }
        }
    }

    /// <summary>
    /// Picks which side of a crossing pair to degrade: the LATER re-routable Auto route
    /// (it was routed with the earlier one already registered), the earlier one when only
    /// that is re-routable, or null when both shapes are forced.
    /// </summary>
    private static WaveguideConnection? PickReroutableSide(
        WaveguideConnection first, WaveguideConnection second)
    {
        static bool IsReroutable(WaveguideConnection c) =>
            c.Type == WaveguideType.Auto && !c.IsRouteFrozen;

        if (IsReroutable(second)) return second;
        if (IsReroutable(first)) return first;
        return null;
    }
}
