using System.Linq;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Routing;
using CAP_Core.Routing.CrossingInsertion;
using Shouldly;
using UnitTests.Routing.CrossingInsertion;
using Xunit;

namespace UnitTests.Routing.CrossingInsertion.ManualCrossing;

/// <summary>
/// Verifies a manually split crossing survives the production post-routing pipeline
/// (<see cref="WaveguideConnectionManager.RecalculateAllTransmissions"/>): the pin-lead
/// auto-collapse pass and the adaptive crossing-insertion pass must leave the user-placed
/// crossing and its two stub connections untouched. Both passes key off
/// <see cref="Component.IsInsertedCrossing"/>/the adaptive registry, which a manual split
/// never populates, so a manual crossing looks like an ordinary placed component to them.
/// </summary>
public class ManualCrossingAutoCollapseInteractionTests
{
    [Fact]
    public void RecalculateAllTransmissions_AfterManualSplit_KeepsCrossingAndStubsIntact()
    {
        var left = CrossingTestCircuit.CreateTerminal("left", 0, 95, pinAngleDegrees: 0);
        var right = CrossingTestCircuit.CreateTerminal("right", 380, 95, pinAngleDegrees: 180);
        var components = new List<Component> { left.Component, right.Component };

        var router = new WaveguideRouter { MinBendRadiusMicrometers = 10.0, AStarCellSize = 4.0 };
        router.InitializePathfindingGrid(0, 0, 400, 400, components);

        var manager = new WaveguideConnectionManager(router)
        {
            CrossingInsertion = new CrossingInsertionService(CrossingTestCircuit.CreateCrossingComponent),
        };

        var original = manager.AddConnection(left.PhysicalPin, right.PhysicalPin);
        original.IsPathValid.ShouldBeTrue("the direct horizontal route must succeed before the split");

        var crossing = CrossingTestCircuit.CreateCrossingComponent();
        double half = CrossingTestCircuit.CrossingEdgeMicrometers / 2.0;
        crossing.PhysicalX = 200 - half;
        crossing.PhysicalY = 100 - half;
        var west = crossing.PhysicalPins.Single(p => p.Name == "port 1");
        var east = crossing.PhysicalPins.Single(p => p.Name == "port 2");

        // Mirrors InsertManualCrossingCommand: remove the original, register the crossing as
        // a component obstacle, dock two fresh sub-connections onto its through ports. The
        // crossing's IsInsertedCrossing is left at its default (false) — a manual crossing is
        // never registered with the adaptive service.
        manager.RemoveConnectionDeferred(original);
        router.AddComponentObstacle(crossing);
        var subA = new WaveguideConnection { StartPin = left.PhysicalPin, EndPin = west };
        var subB = new WaveguideConnection { StartPin = east, EndPin = right.PhysicalPin };
        manager.AddExistingConnection(subA);
        manager.AddExistingConnection(subB);

        // Act: the same production pipeline InsertManualCrossingCommand triggers via
        // RecalculateRoutesAsync — incremental routing, pin-lead collapse, and the
        // adaptive crossing pass all run over the manager's full connection set.
        manager.RecalculateAllTransmissions();

        crossing.IsInsertedCrossing.ShouldBeFalse(
            "a manual crossing must never be marked as an adaptive insertion");
        manager.Connections.Count.ShouldBe(2,
            "the collapse and adaptive passes must not merge, dissolve or add to the manual split");
        manager.Connections.ShouldContain(subA);
        manager.Connections.ShouldContain(subB);
        subA.IsPathValid.ShouldBeTrue("the stub docked at the crossing's west port must route cleanly");
        subB.IsPathValid.ShouldBeTrue("the stub docked at the crossing's east port must route cleanly");
        subA.EndPin.ShouldBeSameAs(west);
        subB.StartPin.ShouldBeSameAs(east);

        // A second pass must be stable: nothing left for the collapse or adaptive pass to
        // still chip away at, so the design does not oscillate on repeated recalculation.
        manager.RecalculateAllTransmissions();
        manager.Connections.Count.ShouldBe(2);
        crossing.IsInsertedCrossing.ShouldBeFalse();
    }
}
