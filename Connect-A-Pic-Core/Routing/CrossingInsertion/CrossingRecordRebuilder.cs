using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;

namespace CAP_Core.Routing.CrossingInsertion;

/// <summary>
/// Reconstructs <see cref="CrossingRecord"/>s from a loaded design: crossing
/// records are runtime bookkeeping and are not persisted, but every crossing the
/// insertion pass placed carries <see cref="Component.IsInsertedCrossing"/> and
/// four sub-connections docked at its ports. From that structure the originals
/// can be derived, so loaded crossings dissolve and re-evaluate exactly like ones
/// inserted in the running session (and are never split again).
/// </summary>
public static class CrossingRecordRebuilder
{
    /// <summary>
    /// Rebuilds and registers records for every auto-inserted crossing component
    /// that has exactly one connection docked at each of its four ports. Crossings
    /// already covered by an active record are skipped, so the call is idempotent.
    /// Returns the number of records rebuilt.
    /// </summary>
    public static int Rebuild(
        CrossingInsertionService service,
        WaveguideConnectionManager manager,
        IEnumerable<Component> components)
    {
        int rebuilt = 0;
        var recordedCrossings = service.Records
            .Select(r => r.CrossingComponent)
            .ToHashSet();

        foreach (var component in components)
        {
            if (!component.IsInsertedCrossing || recordedCrossings.Contains(component))
                continue;

            var record = TryBuildRecord(component, manager.Connections);
            if (record == null) continue;

            service.RestoreRecord(record);
            rebuilt++;
        }
        return rebuilt;
    }

    /// <summary>
    /// Derives the record for one crossing component, or null when the structure
    /// is incomplete (a port without exactly one docked connection) — such a
    /// crossing is left alone as a plain component rather than guessed at.
    /// </summary>
    private static CrossingRecord? TryBuildRecord(
        Component crossing, IReadOnlyList<WaveguideConnection> connections)
    {
        var west = FindSingleDockedConnection(crossing, connections, 180);
        var east = FindSingleDockedConnection(crossing, connections, 0);
        var north = FindSingleDockedConnection(crossing, connections, 270);
        var south = FindSingleDockedConnection(crossing, connections, 90);
        if (west == null || east == null || north == null || south == null)
            return null;

        var originalA = CreateOriginal(crossing, west.Value.Sub, east.Value.Sub);
        var originalB = CreateOriginal(crossing, north.Value.Sub, south.Value.Sub);
        return new CrossingRecord
        {
            CrossingComponent = crossing,
            OriginalA = originalA,
            OriginalB = originalB,
            SubConnectionsA = new List<WaveguideConnection> { west.Value.Sub, east.Value.Sub },
            SubConnectionsB = new List<WaveguideConnection> { north.Value.Sub, south.Value.Sub },
            AnchorPositions = CrossingRecord.CaptureAnchors(originalA, originalB),
        };
    }

    /// <summary>
    /// Finds the single connection docked at the crossing port facing the given
    /// absolute angle. Returns null when the port is missing or when zero or
    /// multiple connections dock there.
    /// </summary>
    private static (WaveguideConnection Sub, PhysicalPin Port)? FindSingleDockedConnection(
        Component crossing, IReadOnlyList<WaveguideConnection> connections, double angleDegrees)
    {
        var port = CrossingPlacement.FindPinByAngle(crossing, angleDegrees);
        if (port == null) return null;

        var docked = connections
            .Where(c => c.StartPin == port || c.EndPin == port)
            .ToList();
        if (docked.Count != 1) return null;
        return (docked[0], port);
    }

    /// <summary>
    /// Recreates the pre-split original connection spanning the two subs' outer
    /// endpoints (the pins NOT on the crossing), inheriting the subs' loss parameters.
    /// </summary>
    private static WaveguideConnection CreateOriginal(
        Component crossing, WaveguideConnection entrySub, WaveguideConnection exitSub)
    {
        return new WaveguideConnection
        {
            StartPin = OuterPin(entrySub, crossing),
            EndPin = OuterPin(exitSub, crossing),
            WidthMicrometers = entrySub.WidthMicrometers,
            BendRadiusMicrometers = entrySub.BendRadiusMicrometers,
            PropagationLossDbPerCm = entrySub.PropagationLossDbPerCm,
            BendLossDbPer90Deg = entrySub.BendLossDbPer90Deg,
            DispersionModel = entrySub.DispersionModel,
        };
    }

    /// <summary>The sub-connection endpoint that is not a port of the crossing.</summary>
    private static PhysicalPin OuterPin(WaveguideConnection sub, Component crossing) =>
        sub.StartPin.ParentComponent == crossing ? sub.EndPin : sub.StartPin;
}
