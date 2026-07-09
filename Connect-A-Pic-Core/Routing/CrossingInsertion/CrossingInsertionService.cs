using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;

namespace CAP_Core.Routing.CrossingInsertion;

/// <summary>
/// Orchestrates adaptive crossing insertion after routing (LiDAR-style):
/// for each detouring connection it evaluates a direct route on a
/// component-only grid, and when that route crosses exactly one other
/// waveguide at a right angle with lower total insertion loss, it places a
/// real PDK crossing component and splits both connections into
/// sub-connections docked at the crossing ports.
/// </summary>
public class CrossingInsertionService
{
    private readonly List<CrossingRecord> _records = new();
    private readonly CrossingInserter _inserter = new();
    private readonly CrossingPlacement _placement = new();

    /// <summary>
    /// Creates the service with a factory producing fresh crossing component
    /// instances (e.g. instantiated from the "Crossing 4-Port" PDK template).
    /// A new instance is requested for every insertion. The factory may return
    /// null when no crossing PDK component is available — in that case the pass
    /// is skipped entirely (conservative: keep detours, never fake a crossing).
    /// </summary>
    public CrossingInsertionService(Func<Component?> crossingComponentFactory)
    {
        CrossingComponentFactory = crossingComponentFactory;
    }

    /// <summary>Factory for fresh crossing component instances (null = PDK crossing unavailable).</summary>
    public Func<Component?> CrossingComponentFactory { get; }

    /// <summary>
    /// Invoked when a crossing component was placed. The host must add it to its
    /// component model (canvas / tile manager) so rendering, export and grid
    /// rebuilds include the crossing.
    /// </summary>
    public Action<Component>? ComponentAdded { get; set; }

    /// <summary>Invoked when a crossing component was removed during dissolution.</summary>
    public Action<Component>? ComponentRemoved { get; set; }

    /// <summary>Safety cap on crossings inserted in one pass.</summary>
    public int MaxCrossingsPerPass { get; set; } = 8;

    /// <summary>Currently active crossings.</summary>
    public IReadOnlyList<CrossingRecord> Records => _records;

    /// <summary>
    /// Runs one crossing-insertion pass over all routed connections.
    /// Called by <see cref="WaveguideConnectionManager"/> after routing completes.
    /// </summary>
    public void InsertBeneficialCrossings(
        WaveguideConnectionManager manager, WaveguideRouter router,
        CancellationToken cancellationToken = default)
    {
        var grid = router.PathfindingGrid;
        if (grid == null) return;

        var draft = CrossingComponentFactory();
        if (draft == null) return; // no crossing PDK component available → keep detours

        double? crossingLossDb = _inserter.GetCrossingThroughLossDb(draft);
        if (crossingLossDb == null) return; // no usable S-matrix → never insert silently

        // A crossing template that lacks any of the four wired ports would pass the
        // through-loss guard (W/E only) but throw during placement, mid-way through a
        // grid mutation — validate all four up front and skip a malformed template
        // entirely (conservative: keep detours, never corrupt the grid) (#553 review).
        if (!_inserter.HasAllFourWiredPorts(draft)) return;

        int inserted = 0;
        bool changed = true;
        while (changed && inserted < MaxCrossingsPerPass && !cancellationToken.IsCancellationRequested)
        {
            changed = false;
            foreach (var connection in manager.Connections.ToList())
            {
                if (cancellationToken.IsCancellationRequested) return;
                if (!TryInsertForConnection(connection, manager, router,
                        draft.WidthMicrometers, crossingLossDb.Value, cancellationToken))
                    continue;
                inserted++;
                changed = true;
                break; // connection list changed — restart the scan
            }
        }
    }

    /// <summary>
    /// Dissolves the crossing a removed connection participates in: all four
    /// sub-connections and the crossing component are removed and the OTHER
    /// original connection is restored unsplit. Returns false when the
    /// connection is not part of any crossing.
    /// </summary>
    public bool TryDissolveForConnection(
        WaveguideConnection removedConnection, WaveguideConnectionManager manager, WaveguideRouter router)
    {
        var record = _records.FirstOrDefault(r => r.ContainsSubConnection(removedConnection));
        if (record == null) return false;

        var removedOriginal = record.GetOriginalFor(removedConnection)!;
        var survivor = removedOriginal == record.OriginalA ? record.OriginalB : record.OriginalA;
        Dissolve(record, manager, router, new List<WaveguideConnection> { survivor });
        return true;
    }

    /// <summary>
    /// Dissolves all crossings whose sub-connections touch the given component
    /// (e.g. because the component is being deleted). Originals not connected to
    /// that component are restored unsplit.
    /// </summary>
    public void DissolveForComponent(
        Component component, WaveguideConnectionManager manager, WaveguideRouter router)
    {
        var affected = _records
            .Where(r => r.AllSubConnections.Any(c => Touches(c, component)) ||
                        r.CrossingComponent == component)
            .ToList();

        foreach (var record in affected)
        {
            var survivors = new List<WaveguideConnection>();
            if (!Touches(record.OriginalA, component)) survivors.Add(record.OriginalA);
            if (!Touches(record.OriginalB, component)) survivors.Add(record.OriginalB);
            Dissolve(record, manager, router, survivors);
        }
    }

    private void Dissolve(
        CrossingRecord record, WaveguideConnectionManager manager, WaveguideRouter router,
        List<WaveguideConnection> survivors)
    {
        _records.Remove(record);
        foreach (var sub in record.AllSubConnections)
        {
            router.PathfindingGrid?.RemoveWaveguideObstacle(sub.Id);
            manager.Connections.Remove(sub);
        }

        router.RemoveComponentObstacle(record.CrossingComponent);

        foreach (var survivor in survivors)
        {
            manager.Connections.Add(survivor);
        }

        // Notify AFTER the connection list is consistent again, so hosts
        // (e.g. the canvas binder) can sync their view of the connections.
        ComponentRemoved?.Invoke(record.CrossingComponent);
    }

    private bool TryInsertForConnection(
        WaveguideConnection connection, WaveguideConnectionManager manager, WaveguideRouter router,
        double crossingEdgeMicrometers, double crossingLossDb, CancellationToken cancellationToken)
    {
        var grid = router.PathfindingGrid!;
        if (connection.RoutedPath == null || !connection.IsPathValid) return false;

        // Never split a sub-connection of an existing crossing again: dissolving
        // the outer crossing would orphan the inner crossing component and its subs.
        if (IsCrossingSubConnection(connection)) return false;

        double detourLossDb = connection.IsBlockedFallback
            ? double.PositiveInfinity
            : connection.TotalLossDb;

        // Cheap pre-filter: even a perfectly straight direct route cannot beat the
        // current detour by more than (detour − straight-line) dB. If that gain is
        // below one crossing's through-loss, a crossing can never win.
        if (!double.IsPositiveInfinity(detourLossDb) &&
            detourLossDb - StraightLineLossDb(connection) < crossingLossDb)
            return false;

        // Route the direct path on a component-only grid (waveguides removed) so it passes
        // THROUGH other waveguides — the crossing is detected geometrically afterwards. The
        // grid is mutated here, so any failure past this point must restore it (see catch).
        grid.ClearAllWaveguideObstacles();
        try
        {
            var directPath = router.Route(connection.StartPin, connection.EndPin, cancellationToken);

            // Re-register all other routed connections as obstacles for the checks below.
            var others = manager.Connections.Where(c => c != connection).ToList();
            foreach (var other in others)
            {
                if (other.IsPathValid && other.RoutedPath != null)
                    grid.AddWaveguideObstacle(other.Id, other.RoutedPath.Segments, manager.WaveguideWidthMicrometers);
            }

            CrossingCandidate? candidate = null;
            if (directPath.IsValid && !directPath.IsBlockedFallback && !directPath.IsInvalidGeometry)
            {
                candidate = _inserter.FindCandidate(
                    connection, directPath, others, grid, crossingEdgeMicrometers, crossingLossDb);

                // Never cross a sub-connection of an existing crossing (see guard above).
                if (candidate != null && IsCrossingSubConnection(candidate.ExistingConnection))
                    candidate = null;
            }

            Component? crossing = null;
            if (candidate != null && _inserter.IsCrossingBeneficial(candidate, detourLossDb))
                crossing = CrossingComponentFactory();

            if (crossing == null)
            {
                // Keep the detour: restore this connection's own obstacle.
                grid.AddWaveguideObstacle(connection.Id, connection.RoutedPath.Segments,
                    manager.WaveguideWidthMicrometers);
                return false;
            }

            ApplyInsertion(candidate!, crossing, manager, router);
            return true;
        }
        catch
        {
            // Defense-in-depth: a throw after ClearAllWaveguideObstacles must never leave the
            // grid half-populated for the NEXT routing pass. Rebuild obstacles from the current
            // connection set, then let the error surface.
            RebuildAllWaveguideObstacles(manager, router);
            throw;
        }
    }

    /// <summary>
    /// Rebuilds the pathfinding grid's waveguide obstacles from the manager's current
    /// connection set — used to recover a consistent grid if a crossing pass throws
    /// after clearing obstacles.
    /// </summary>
    private static void RebuildAllWaveguideObstacles(WaveguideConnectionManager manager, WaveguideRouter router)
    {
        var grid = router.PathfindingGrid;
        if (grid == null) return;
        grid.ClearAllWaveguideObstacles();
        foreach (var c in manager.Connections)
        {
            if (c.IsPathValid && c.RoutedPath != null)
                grid.AddWaveguideObstacle(c.Id, c.RoutedPath.Segments, manager.WaveguideWidthMicrometers);
        }
    }

    private void ApplyInsertion(
        CrossingCandidate candidate, Component crossing,
        WaveguideConnectionManager manager, WaveguideRouter router)
    {
        var grid = router.PathfindingGrid!;
        crossing.Name = $"{crossing.Name}_{Guid.NewGuid().ToString("N")[..8]}";
        var record = _placement.Place(candidate, crossing);

        grid.RemoveWaveguideObstacle(record.OriginalA.Id);
        grid.RemoveWaveguideObstacle(record.OriginalB.Id);
        manager.Connections.Remove(record.OriginalA);
        manager.Connections.Remove(record.OriginalB);

        router.AddComponentObstacle(crossing);

        foreach (var sub in record.AllSubConnections)
        {
            manager.Connections.Add(sub);
            sub.RecalculateTransmission(router);
            if (sub.IsPathValid && sub.RoutedPath != null)
                grid.AddWaveguideObstacle(sub.Id, sub.RoutedPath.Segments, manager.WaveguideWidthMicrometers);
        }

        _records.Add(record);

        // Notify AFTER the sub-connections replaced the originals, so hosts
        // (e.g. the canvas binder) see a consistent connection list.
        ComponentAdded?.Invoke(crossing);
    }

    private static double StraightLineLossDb(WaveguideConnection connection)
    {
        var (x1, y1) = connection.StartPin.GetAbsolutePosition();
        var (x2, y2) = connection.EndPin.GetAbsolutePosition();
        double distanceMicrometers = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
        double lossDbPerCm = connection.DispersionModel?.LossDbPerCmAt(CrossingInserter.ReferenceWavelengthNm)
                             ?? connection.PropagationLossDbPerCm;
        return distanceMicrometers / 10000.0 * lossDbPerCm;
    }

    /// <summary>True when the connection is a sub-connection of any active crossing.</summary>
    private bool IsCrossingSubConnection(WaveguideConnection connection) =>
        _records.Any(r => r.ContainsSubConnection(connection));

    private static bool Touches(WaveguideConnection connection, Component component) =>
        connection.StartPin.ParentComponent == component ||
        connection.EndPin.ParentComponent == component;
}
