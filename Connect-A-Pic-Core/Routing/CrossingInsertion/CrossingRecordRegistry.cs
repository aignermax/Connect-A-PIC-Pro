using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;

namespace CAP_Core.Routing.CrossingInsertion;

/// <summary>
/// Owns the bookkeeping of active <see cref="CrossingRecord"/>s and implements
/// crossing dissolution: removing the four sub-connections and the crossing
/// component, and restoring the original (unsplit) connections. All structural
/// mutations of the shared connection list happen under the manager's
/// <see cref="WaveguideConnectionManager.SyncRoot"/> lock so UI-thread consumers
/// never observe a half-dissolved state.
/// </summary>
public class CrossingRecordRegistry
{
    private readonly List<CrossingRecord> _records = new();

    /// <summary>Invoked after a crossing component was removed during dissolution.</summary>
    public Action<Component>? ComponentRemoved { get; set; }

    /// <summary>Currently active crossings.</summary>
    public IReadOnlyList<CrossingRecord> Records => _records;

    /// <summary>Registers a newly placed crossing.</summary>
    public void Add(CrossingRecord record) => _records.Add(record);

    /// <summary>
    /// Drops all records without touching connections or components. Called when
    /// the whole design is discarded (File → New, project load, group-edit swap)
    /// so stale records can never resurrect connections into a fresh design.
    /// </summary>
    public void Reset() => _records.Clear();

    /// <summary>True when the connection is a sub-connection of any active crossing.</summary>
    public bool IsSubConnection(WaveguideConnection connection) =>
        _records.Any(r => r.ContainsSubConnection(connection));

    /// <summary>True when the connection participates in any active crossing (as sub or original).</summary>
    public bool IsCrossingConnection(WaveguideConnection connection) =>
        _records.Any(r => r.ContainsSubConnection(connection) ||
                          r.OriginalA == connection || r.OriginalB == connection);

    /// <summary>
    /// Maps a crossing sub-connection back to its pre-split original connection.
    /// Returns the connection itself when it is not a sub-connection.
    /// </summary>
    public WaveguideConnection ResolveToOriginal(WaveguideConnection connection)
    {
        foreach (var record in _records)
        {
            var original = record.GetOriginalFor(connection);
            if (original != null) return original;
        }
        return connection;
    }

    /// <summary>
    /// Dissolves the crossing the removed connection participates in (as a
    /// sub-connection or as a split original): all four sub-connections and the
    /// crossing component are removed and the OTHER original connection is
    /// restored unsplit. Returns false when the connection is not part of any crossing.
    /// </summary>
    public bool TryDissolveForConnection(
        WaveguideConnection removedConnection, WaveguideConnectionManager manager, WaveguideRouter router)
    {
        var record = _records.FirstOrDefault(r =>
            r.ContainsSubConnection(removedConnection) ||
            r.OriginalA == removedConnection || r.OriginalB == removedConnection);
        if (record == null) return false;

        var removedOriginal = record.GetOriginalFor(removedConnection) ?? removedConnection;
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

    /// <summary>
    /// Dissolves every crossing whose anchor pins moved since placement (a net
    /// endpoint was dragged). Both originals are restored so the next routing +
    /// crossing pass re-evaluates them from scratch — the crossing is re-inserted
    /// only if it is still geometrically valid and beneficial.
    /// </summary>
    public void DissolveStaleRecords(WaveguideConnectionManager manager, WaveguideRouter router)
    {
        foreach (var record in _records.Where(r => r.HaveAnchorsMoved()).ToList())
        {
            Dissolve(record, manager, router,
                new List<WaveguideConnection> { record.OriginalA, record.OriginalB });
        }
    }

    /// <summary>
    /// Removes the record's sub-connections and crossing component, restores the
    /// given survivors into the connection list, and notifies the host.
    /// </summary>
    private void Dissolve(
        CrossingRecord record, WaveguideConnectionManager manager, WaveguideRouter router,
        List<WaveguideConnection> survivors)
    {
        _records.Remove(record);
        lock (manager.SyncRoot)
        {
            foreach (var sub in record.AllSubConnections)
            {
                router.PathfindingGrid?.RemoveWaveguideObstacle(sub.Id);
                manager.Connections.Remove(sub);
            }

            router.RemoveComponentObstacle(record.CrossingComponent);

            foreach (var survivor in survivors)
            {
                if (!manager.Connections.Contains(survivor))
                    manager.Connections.Add(survivor);
            }
        }

        // Notify AFTER the connection list is consistent again, so hosts
        // (e.g. the canvas binder) can sync their view of the connections.
        ComponentRemoved?.Invoke(record.CrossingComponent);
    }

    private static bool Touches(WaveguideConnection connection, Component component) =>
        connection.StartPin.ParentComponent == component ||
        connection.EndPin.ParentComponent == component;
}
