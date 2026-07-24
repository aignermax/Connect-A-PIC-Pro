using CAP_Core.Components.Core;
using CAP_Core.Routing.CrossingInsertion;
using Shouldly;
using Xunit;

namespace UnitTests.Routing.CrossingInsertion;

/// <summary>
/// Lifecycle tests for adaptive crossing insertion (#705): every removal path
/// dissolves the crossing, stale crossings re-evaluate after endpoints move,
/// failed sub-routing rolls back, records reset on Clear and rebuild after load.
/// </summary>
public class CrossingLifecycleTests
{
    /// <summary>Bend loss that makes the detour clearly worse than one crossing.</summary>
    private const double ExpensiveBendLossDb = 0.5;

    [Fact]
    public void RemoveConnectionDeferred_OnSubConnection_DissolvesCrossingAndRestoresSurvivor()
    {
        var layout = CrossingTestCircuit.Build(ExpensiveBendLossDb);
        var record = layout.Service.Records.ShouldHaveSingleItem();

        // The UI delete path (DeleteConnectionCommand, pin cleanup) uses the
        // deferred removal — it must dissolve exactly like RemoveConnection.
        layout.Manager.RemoveConnectionDeferred(record.SubConnectionsB[0]);

        layout.RemovedCrossings.ShouldContain(record.CrossingComponent,
            "the crossing component must not be orphaned");
        layout.Service.Records.ShouldBeEmpty();
        var survivor = layout.Manager.Connections.ShouldHaveSingleItem();
        survivor.ShouldBeSameAs(record.OriginalA, "the untouched net must be restored unsplit");
    }

    [Fact]
    public void RemoveConnectionDeferred_OnSplitOriginal_DissolvesCrossingAndRestoresOther()
    {
        var layout = CrossingTestCircuit.Build(ExpensiveBendLossDb);
        var record = layout.Service.Records.ShouldHaveSingleItem();

        // Undo of CreateConnectionCommand removes the ORIGINAL connection object,
        // which the crossing pass replaced with sub-connections — dissolution must
        // recognize split originals too, not only the subs.
        layout.Manager.RemoveConnectionDeferred(record.OriginalA);

        layout.RemovedCrossings.ShouldContain(record.CrossingComponent);
        layout.Service.Records.ShouldBeEmpty();
        var survivor = layout.Manager.Connections.ShouldHaveSingleItem();
        survivor.ShouldBeSameAs(record.OriginalB);
    }

    [Fact]
    public void Clear_ResetsCrossingRecordsWithConnections()
    {
        var layout = CrossingTestCircuit.Build(ExpensiveBendLossDb);
        layout.Service.Records.ShouldNotBeEmpty();

        // File → New / project load / group-edit swap discard the whole design.
        layout.Manager.Clear();

        layout.Manager.Connections.ShouldBeEmpty();
        layout.Service.Records.ShouldBeEmpty(
            "stale records would dissolve against connections of the NEXT design");
    }

    [Fact]
    public void MovedNetEndpoint_StaleCrossingDissolvesOnRecalculate()
    {
        var layout = CrossingTestCircuit.Build(ExpensiveBendLossDb);
        var record = layout.Service.Records.ShouldHaveSingleItem();

        // Move the vertical net's bottom terminal ABOVE the horizontal net, so
        // the nets no longer intersect and the crossing has no reason to exist.
        layout.BBottom.Component.PhysicalY = 60;

        layout.Manager.RecalculateAllTransmissions();

        layout.RemovedCrossings.ShouldContain(record.CrossingComponent,
            "a crossing whose net endpoints moved must be re-evaluated, not kept forever");
        layout.Service.Records.ShouldBeEmpty();
        layout.Manager.Connections.Count.ShouldBe(2, "both nets must be restored unsplit");
        foreach (var connection in layout.Manager.Connections)
            connection.IsPathValid.ShouldBeTrue();
    }

    [Fact]
    public void UnroutableCrossingPort_RollsBackInsertionAndKeepsDetour()
    {
        // A 100x100 wall away from both nets: the sabotaged crossing's north port
        // lands at its center, deeper inside than the router's pin-corridor
        // clearing reaches, so that sub-connection can never route.
        var wall = CrossingTestCircuit.CreateTerminal("wall", 300, 200, 0);
        wall.Component.WidthMicrometers = 100;
        wall.Component.HeightMicrometers = 100;

        var layout = CrossingTestCircuit.Build(
            ExpensiveBendLossDb, CreateCrossingWithBuriedPort,
            extraComponents: new[] { wall.Component });

        layout.AddedCrossings.ShouldBeEmpty(
            "a failed insertion must never announce or keep the crossing");
        layout.Service.Records.ShouldBeEmpty();
        layout.Manager.Connections.Count.ShouldBe(2, "the originals must be restored");
        foreach (var connection in layout.Manager.Connections)
        {
            connection.IsPathValid.ShouldBeTrue("the working detour must survive the rollback");
            connection.IsBlockedFallback.ShouldBeFalse();
        }
    }

    [Fact]
    public void Rebuild_RestoresRecordsForLoadedCrossings_AndIsIdempotent()
    {
        var layout = CrossingTestCircuit.Build(ExpensiveBendLossDb);
        var crossing = layout.AddedCrossings.ShouldHaveSingleItem();

        // Records are runtime-only: simulate save → load by dropping them while
        // the crossing component (flagged IsInsertedCrossing) and subs remain.
        layout.Service.Reset();
        crossing.IsInsertedCrossing.ShouldBeTrue("the marker must be set for persistence");

        var components = AllComponents(layout, crossing);
        CrossingRecordRebuilder.Rebuild(layout.Service, layout.Manager, components).ShouldBe(1);
        CrossingRecordRebuilder.Rebuild(layout.Service, layout.Manager, components)
            .ShouldBe(0, "already-recorded crossings must be skipped");

        var record = layout.Service.Records.ShouldHaveSingleItem();
        record.CrossingComponent.ShouldBeSameAs(crossing);
        record.AllSubConnections.ShouldBe(layout.Manager.Connections, ignoreOrder: true);
    }

    [Fact]
    public void RebuiltRecord_DissolvesLikeSessionInsertedCrossing()
    {
        var layout = CrossingTestCircuit.Build(ExpensiveBendLossDb);
        var crossing = layout.AddedCrossings.ShouldHaveSingleItem();
        layout.Service.Reset();
        CrossingRecordRebuilder.Rebuild(
            layout.Service, layout.Manager, AllComponents(layout, crossing)).ShouldBe(1);
        var record = layout.Service.Records.ShouldHaveSingleItem();

        // Deleting a sub of a LOADED crossing must dissolve exactly like in the
        // session that inserted it.
        layout.Manager.RemoveConnectionDeferred(record.SubConnectionsB[0]);

        layout.RemovedCrossings.ShouldContain(crossing);
        layout.Service.Records.ShouldBeEmpty();
        var survivor = layout.Manager.Connections.ShouldHaveSingleItem();
        new[] { survivor.StartPin, survivor.EndPin }.ShouldBe(
            new[] { layout.ALeft.PhysicalPin, layout.ARight.PhysicalPin }, ignoreOrder: true,
            customMessage: "the rebuilt original must span the horizontal net's outer pins");
    }

    /// <summary>All design components including the inserted crossing.</summary>
    private static List<Component> AllComponents(
        CrossingTestCircuit.CrossLayout layout, Component crossing) => new()
    {
        layout.ALeft.Component, layout.ARight.Component,
        layout.BTop.Component, layout.BBottom.Component, crossing,
    };

    /// <summary>
    /// A crossing whose north port is displaced to (350, 250) — the center of the
    /// wall placed at (300, 200)..(400, 300) — once the crossing is centered on
    /// the (200, 100) intersection. The port sits 50 µm inside the wall, beyond
    /// the router's 30 µm pin-corridor clearing, so the north sub-connection is
    /// unroutable and the insertion must roll back.
    /// </summary>
    private static Component CreateCrossingWithBuriedPort()
    {
        var crossing = CrossingTestCircuit.CreateCrossingComponent();
        var north = crossing.PhysicalPins.First(p => p.AngleDegrees == 270);
        north.OffsetXMicrometers = 350.0 - (200.0 - CrossingTestCircuit.CrossingEdgeMicrometers / 2.0);
        north.OffsetYMicrometers = 250.0 - (100.0 - CrossingTestCircuit.CrossingEdgeMicrometers / 2.0);
        return crossing;
    }
}
