using CAP.Avalonia.Commands;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Canvas.CrossingInsertion;
using Shouldly;
using UnitTests.Routing.CrossingInsertion;
using Xunit;

namespace UnitTests.ViewModels;

/// <summary>
/// Undo/redo tests with active crossing insertion (#705): connection and
/// component commands must dissolve crossings instead of leaving orphaned
/// crossing components, and undo must never resurrect sub-connections of a
/// dissolved crossing (ghost pins, duplicate connectivity).
/// </summary>
public class CrossingUndoRedoTests
{
    /// <summary>Bend loss that makes the detour clearly worse than one crossing.</summary>
    private const double ExpensiveBendLossDb = 0.5;

    private sealed record Fixture(
        DesignCanvasViewModel Canvas, CrossingInsertionCanvasBinder Binder,
        CrossingTestCircuit.Terminal ALeft, CrossingTestCircuit.Terminal ARight,
        CrossingTestCircuit.Terminal BTop, CrossingTestCircuit.Terminal BBottom);

    /// <summary>Canvas with crossing insertion enabled and four terminals placed.</summary>
    private static Fixture BuildCanvas(bool connectHorizontalNet)
    {
        var canvas = new DesignCanvasViewModel();
        canvas.InitializeAStarRouting(0, 0, 400, 400);
        canvas.ConnectionManager.DefaultBendLossDbPer90Deg = ExpensiveBendLossDb;

        var binder = new CrossingInsertionCanvasBinder(
            canvas,
            () => new CrossingComponentInstance(
                CrossingTestCircuit.CreateCrossingComponent(), "Crossing 4-Port", "SiEPIC EBeam"),
            uiDispatch: action => action())
        {
            IsEnabled = true,
        };

        var aLeft = CrossingTestCircuit.CreateTerminal("A_left", 0, 95, 0, sourceCoupling: 1.0);
        var aRight = CrossingTestCircuit.CreateTerminal("A_right", 390, 95, 180);
        var bTop = CrossingTestCircuit.CreateTerminal("B_top", 195, 40, 90);
        var bBottom = CrossingTestCircuit.CreateTerminal("B_bottom", 195, 350, 270);
        foreach (var terminal in new[] { aLeft, aRight, bTop, bBottom })
            canvas.AddComponent(terminal.Component);

        canvas.ConnectPins(bTop.PhysicalPin, bBottom.PhysicalPin);
        if (connectHorizontalNet)
            canvas.ConnectPins(aLeft.PhysicalPin, aRight.PhysicalPin);
        canvas.ConnectionManager.RecalculateAllTransmissions();

        return new Fixture(canvas, binder, aLeft, aRight, bTop, bBottom);
    }

    /// <summary>
    /// The canvas connection view-models must mirror the manager, all endpoints
    /// must belong to components present on the canvas (no ghost pins), and no
    /// connection may appear twice (no duplicate connectivity).
    /// </summary>
    private static void AssertCanvasIsConsistent(DesignCanvasViewModel canvas)
    {
        var managed = canvas.ConnectionManager.Connections.ToList();
        managed.Distinct().Count().ShouldBe(managed.Count, "no duplicate connections");
        canvas.Connections.Select(vm => vm.Connection).ShouldBe(managed, ignoreOrder: true);

        var placed = canvas.Components.Select(vm => vm.Component).ToHashSet();
        foreach (var connection in managed)
        {
            placed.ShouldContain(connection.StartPin.ParentComponent, "no ghost start pins");
            placed.ShouldContain(connection.EndPin.ParentComponent, "no ghost end pins");
        }
    }

    /// <summary>Waits until all routing triggered by the commands has settled.</summary>
    private static Task SettleAsync(DesignCanvasViewModel canvas) => canvas.RecalculateRoutesAsync();

    [Fact]
    public async Task DeleteConnectionCommand_OnCrossingSub_DissolvesAndUndoRestoresNet()
    {
        var fixture = BuildCanvas(connectHorizontalNet: true);
        var record = fixture.Binder.Service.Records.ShouldHaveSingleItem();
        var subVm = fixture.Canvas.Connections.First(
            vm => vm.Connection == record.SubConnectionsB[0]);
        var command = new DeleteConnectionCommand(fixture.Canvas, subVm);

        command.Execute();
        await SettleAsync(fixture.Canvas);

        fixture.Canvas.Components.ShouldNotContain(
            vm => vm.Component == record.CrossingComponent,
            "deleting a sub-connection must dissolve the crossing");
        AssertCanvasIsConsistent(fixture.Canvas);

        command.Undo();
        await SettleAsync(fixture.Canvas);

        // Both nets exist again; the routing pass re-evaluates and re-inserts.
        fixture.Binder.Service.Records.Count.ShouldBe(1);
        AssertCanvasIsConsistent(fixture.Canvas);
    }

    [Fact]
    public async Task CreateConnectionCommand_UndoAfterCrossingSplit_RemovesNetCompletely()
    {
        var fixture = BuildCanvas(connectHorizontalNet: false);
        var command = new CreateConnectionCommand(
            fixture.Canvas, fixture.ALeft.PhysicalPin, fixture.ARight.PhysicalPin);

        command.Execute();
        await SettleAsync(fixture.Canvas);
        fixture.Binder.Service.Records.Count.ShouldBe(1, "the new net must trigger an insertion");

        command.Undo();
        await SettleAsync(fixture.Canvas);

        fixture.Binder.Service.Records.ShouldBeEmpty(
            "undoing the connection that caused the crossing must dissolve it");
        var survivor = fixture.Canvas.ConnectionManager.Connections.ShouldHaveSingleItem();
        new[] { survivor.StartPin, survivor.EndPin }.ShouldBe(
            new[] { fixture.BTop.PhysicalPin, fixture.BBottom.PhysicalPin }, ignoreOrder: true,
            customMessage: "the vertical net must be restored unsplit");
        AssertCanvasIsConsistent(fixture.Canvas);

        // Redo re-creates the net; the pass re-inserts a crossing.
        command.Execute();
        await SettleAsync(fixture.Canvas);
        fixture.Binder.Service.Records.Count.ShouldBe(1);
        AssertCanvasIsConsistent(fixture.Canvas);
    }

    [Fact]
    public async Task DeleteComponentCommand_UndoAfterDissolve_RestoresBothNetsWithoutDuplicates()
    {
        var fixture = BuildCanvas(connectHorizontalNet: true);
        var record = fixture.Binder.Service.Records.ShouldHaveSingleItem();
        var terminalVm = fixture.Canvas.Components.First(
            vm => vm.Component == fixture.ARight.Component);
        var command = new DeleteComponentCommand(fixture.Canvas, terminalVm);

        command.Execute();
        await SettleAsync(fixture.Canvas);

        fixture.Canvas.Components.ShouldNotContain(
            vm => vm.Component == record.CrossingComponent,
            "deleting a net endpoint must dissolve the crossing");
        AssertCanvasIsConsistent(fixture.Canvas);

        command.Undo();
        await SettleAsync(fixture.Canvas);

        fixture.Binder.Service.Records.Count.ShouldBe(1,
            "restored nets must be re-evaluated and split again");
        AssertCanvasIsConsistent(fixture.Canvas);
    }
}
