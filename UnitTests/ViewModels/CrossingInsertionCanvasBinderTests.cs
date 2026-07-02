using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Canvas.CrossingInsertion;
using Shouldly;
using UnitTests.Routing.CrossingInsertion;
using Xunit;

namespace UnitTests.ViewModels;

/// <summary>
/// Verifies the production wiring of adaptive crossing insertion (Issue #553):
/// the <see cref="CrossingInsertionCanvasBinder"/> attaches the service to the
/// canvas' connection manager and keeps the canvas component / pin / connection
/// view-models in sync when a crossing is inserted or dissolved.
/// </summary>
public class CrossingInsertionCanvasBinderTests
{
    /// <summary>Bend loss that makes the detour clearly worse than one crossing.</summary>
    private const double ExpensiveBendLossDb = 0.5;

    private static (DesignCanvasViewModel Canvas, CrossingInsertionCanvasBinder Binder,
        CrossingTestCircuit.Terminal ALeft, CrossingTestCircuit.Terminal ARight,
        CrossingTestCircuit.Terminal BTop, CrossingTestCircuit.Terminal BBottom)
        BuildCanvasWithCrossedNets()
    {
        var canvas = new DesignCanvasViewModel();
        canvas.InitializeAStarRouting(0, 0, 400, 400);
        canvas.ConnectionManager.DefaultBendLossDbPer90Deg = ExpensiveBendLossDb;

        var binder = new CrossingInsertionCanvasBinder(
            canvas,
            () => new CrossingComponentInstance(
                CrossingTestCircuit.CreateCrossingComponent(), "Crossing 4-Port", "SiEPIC EBeam"),
            uiDispatch: action => action());

        var aLeft = CrossingTestCircuit.CreateTerminal("A_left", 0, 95, 0, sourceCoupling: 1.0);
        var aRight = CrossingTestCircuit.CreateTerminal("A_right", 390, 95, 180);
        var bTop = CrossingTestCircuit.CreateTerminal("B_top", 195, 40, 90);
        var bBottom = CrossingTestCircuit.CreateTerminal("B_bottom", 195, 350, 270);
        foreach (var terminal in new[] { aLeft, aRight, bTop, bBottom })
            canvas.AddComponent(terminal.Component);

        canvas.ConnectPins(bTop.PhysicalPin, bBottom.PhysicalPin);
        canvas.ConnectPins(aLeft.PhysicalPin, aRight.PhysicalPin);
        canvas.ConnectionManager.RecalculateAllTransmissions();

        return (canvas, binder, aLeft, aRight, bTop, bBottom);
    }

    [Fact]
    public void Binder_AttachesServiceToConnectionManager()
    {
        var canvas = new DesignCanvasViewModel();
        var binder = new CrossingInsertionCanvasBinder(
            canvas, () => null, uiDispatch: action => action());

        canvas.ConnectionManager.CrossingInsertion.ShouldBeSameAs(binder.Service);
    }

    [Fact]
    public void InsertedCrossing_AppearsAsComponentPinAndConnectionViewModels()
    {
        var (canvas, binder, _, _, _, _) = BuildCanvasWithCrossedNets();

        var record = binder.Service.Records.ShouldHaveSingleItem();

        var crossingVm = canvas.Components
            .Single(vm => vm.Component == record.CrossingComponent);
        crossingVm.TemplateName.ShouldBe("Crossing 4-Port");
        crossingVm.TemplatePdkSource.ShouldBe("SiEPIC EBeam");

        canvas.AllPins.Count(p => p.ParentComponentViewModel == crossingVm)
            .ShouldBe(4, "all four crossing ports must be selectable on the canvas");

        // Connection VMs must mirror the manager: 4 sub-connections, no stale originals.
        canvas.Connections.Count.ShouldBe(4);
        canvas.Connections.Select(vm => vm.Connection)
            .ShouldBe(canvas.ConnectionManager.Connections, ignoreOrder: true);
    }

    [Fact]
    public void RemovingCrossedConnection_RemovesCrossingViewModels()
    {
        var (canvas, binder, _, _, _, _) = BuildCanvasWithCrossedNets();
        var record = binder.Service.Records.ShouldHaveSingleItem();
        var subToRemove = record.SubConnectionsB[0];

        canvas.ConnectionManager.RemoveConnection(subToRemove);

        canvas.Components.ShouldNotContain(vm => vm.Component == record.CrossingComponent,
            "the dissolved crossing must disappear from the canvas");
        canvas.AllPins.ShouldNotContain(p => p.Pin.ParentComponent == record.CrossingComponent);
        canvas.Connections.Select(vm => vm.Connection)
            .ShouldBe(canvas.ConnectionManager.Connections, ignoreOrder: true);
    }

    [Fact]
    public void NullFactory_KeepsDetourAndAddsNothing()
    {
        var canvas = new DesignCanvasViewModel();
        canvas.InitializeAStarRouting(0, 0, 400, 400);
        canvas.ConnectionManager.DefaultBendLossDbPer90Deg = ExpensiveBendLossDb;
        var binder = new CrossingInsertionCanvasBinder(
            canvas, () => null, uiDispatch: action => action());

        var bTop = CrossingTestCircuit.CreateTerminal("B_top", 195, 40, 90);
        var bBottom = CrossingTestCircuit.CreateTerminal("B_bottom", 195, 350, 270);
        var aLeft = CrossingTestCircuit.CreateTerminal("A_left", 0, 95, 0);
        var aRight = CrossingTestCircuit.CreateTerminal("A_right", 390, 95, 180);
        foreach (var terminal in new[] { aLeft, aRight, bTop, bBottom })
            canvas.AddComponent(terminal.Component);
        canvas.ConnectPins(bTop.PhysicalPin, bBottom.PhysicalPin);
        canvas.ConnectPins(aLeft.PhysicalPin, aRight.PhysicalPin);

        canvas.ConnectionManager.RecalculateAllTransmissions();

        binder.Service.Records.ShouldBeEmpty("no PDK crossing available → detour must be kept");
        canvas.Components.Count.ShouldBe(4);
        canvas.Connections.Count.ShouldBe(2);
    }
}
