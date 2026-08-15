using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Routing;
using CAP_Core.Routing.AStarPathfinder;
using CAP_Core.Routing.InterconnectRouting.SegmentShift;
using Shouldly;
using Xunit;

namespace UnitTests.Routing;

/// <summary>
/// Grid ownership regressions (issue #801): a neighbouring component whose PADDING band
/// reaches across a foreign pin corridor re-marks those corridor cells as blocked. A
/// collapsed, manually edited route whose bend hugs its own pin inside that corridor must
/// survive — unfreezing would silently discard the user's edit, and a false
/// <c>PassesThroughComponent</c> flag would cry wolf during shift drags. A foreign
/// component BODY inside the corridor is a real collision and still blocks.
/// </summary>
public class PinCorridorOwnershipTests
{
    /// <summary>Bend radius (µm) of the U-turn fixtures, mirroring the collapse hardening tests.</summary>
    private const double Radius = 10.0;

    /// <summary>Residual tolerance for a "collapsed to the pin" lead (floating-point noise).</summary>
    private const double CollapsedTolerance = 1e-3;

    [Fact]
    public void ForeignPaddingOverPinCorridor_DoesNotUnfreezeCollapsedFrozenRoute()
    {
        var (manager, router, connection, startPin, _) = RouteUTurn();
        StartPinLead(connection.RoutedPath!, startPin).ShouldBe(0, CollapsedTolerance,
            "precondition: the departure lead is collapsed onto the pin");
        ShiftFirstShiftableStraight(connection);
        connection.IsRouteFrozen.ShouldBeTrue("precondition: the manual shift freezes the route");
        var frozenPath = connection.RoutedPath!;
        var offsets = new Dictionary<int, double>(connection.StraightShiftOffsets);

        // Body at x 50–54, y 16–20; only its 5 µm padding band reaches the corridor cells.
        router.AddComponentObstacle(TestComponentFactory.CreatePinlessComponent(50, 16, width: 4, height: 4));
        manager.RecalculateAllTransmissions();

        connection.IsRouteFrozen.ShouldBeTrue(
            "a neighbour's padding over the own pin corridor is no collision — unfreezing would destroy the manual edit");
        connection.RoutedPath.ShouldBeSameAs(frozenPath);
        connection.StraightShiftOffsets.Count.ShouldBe(offsets.Count);
        foreach (var (index, offset) in offsets)
            connection.StraightShiftOffsets[index].ShouldBe(offset, 1e-9);
    }

    [Fact]
    public void ForeignPaddingOverPinCorridor_DoesNotFlagCollisionDuringShiftDrag()
    {
        var (_, router, connection, _, _) = RouteUTurn();
        ShiftFirstShiftableStraight(connection);

        router.AddComponentObstacle(TestComponentFactory.CreatePinlessComponent(50, 16, width: 4, height: 4));
        SegmentShiftEditor.RefreshComponentCollision(connection, router);

        connection.RoutedPath!.PassesThroughComponent.ShouldBeFalse(
            "a bend hugging its own pin inside the pin corridor is not a component collision, "
            + "even when a neighbour's padding band covers the corridor cells");
    }

    [Fact]
    public void ForeignBodyInsidePinCorridor_StillUnfreezesCollapsedFrozenRoute()
    {
        var (manager, router, connection, _, _) = RouteUTurn();
        ShiftFirstShiftableStraight(connection);

        // Body lands INSIDE the corridor zone (same fixture as the collapse hardening tests).
        router.AddComponentObstacle(TestComponentFactory.CreatePinlessComponent(53, 23, width: 17, height: 4));
        manager.RecalculateAllTransmissions();

        connection.IsRouteFrozen.ShouldBeFalse(
            "a foreign body over the pin corridor is a real collision and must unfreeze the route");
        connection.StraightShiftOffsets.ShouldBeEmpty("the manual edit is discarded like on any collision");
        connection.IsPathValid.ShouldBeTrue();
    }

    [Fact]
    public void GridOwnership_PaddingOverForeignCorridorIsTolerated_BodyStillBlocks()
    {
        var grid = new PathfindingGrid(-100, -100, 500, 500, cellSize: 4.0, padding: 5.0);
        var owner = TestComponentFactory.CreatePinlessComponent(0, 0);
        var pin = TestComponentFactory.CreateRoutingPin(owner, 50, 25, 0);
        owner.PhysicalPins.Add(pin);
        grid.AddComponentObstacle(owner);

        var corridors = grid.GetPinCorridorCells(new[] { pin });
        corridors.ShouldNotBeEmpty("the registered pin carves its persistent corridor");

        // Corridor cell (38, 31): the padding-only neighbour claims it, so the plain verdict
        // reports blocked while the corridor-tolerant verdict does not.
        grid.AddComponentObstacle(TestComponentFactory.CreatePinlessComponent(54, 16, width: 8, height: 4));
        corridors.ShouldContain((38, 31));
        grid.GetCellState(38, 31).ShouldBe((byte)1);
        grid.IsBlockedByComponentOnly(38, 31).ShouldBeTrue();
        grid.IsBlockedByComponentOnly(38, 31, corridors).ShouldBeFalse(
            "only foreign padding reaches across the corridor cell");
        grid.IsBlockedByComponent(38, 31, corridors).ShouldBeFalse();

        // A foreign body over the same corridor cell must keep blocking, tolerance or not.
        grid.AddComponentObstacle(TestComponentFactory.CreatePinlessComponent(53, 23, width: 17, height: 4));
        grid.IsBlockedByComponentOnly(38, 31, corridors).ShouldBeTrue(
            "a foreign component body inside the corridor is a real collision");
        grid.IsBlockedByComponent(38, 31, corridors).ShouldBeTrue();
    }

    /// <summary>Shifts the first shiftable straight, trying both normal directions so the
    /// test is robust against the router's orientation of the middle straight.</summary>
    private static void ShiftFirstShiftableStraight(WaveguideConnection connection)
    {
        var handles = SegmentShiftGeometry.GetHandles(connection.GetPathSegments());
        handles.ShouldNotBeEmpty("the collapsed route must expose a shiftable straight");
        var handle = handles[0];

        if (!SegmentShiftEditor.TryApplyShift(connection, handle.StraightIndex, 4.0, out _) &&
            !SegmentShiftEditor.TryApplyShift(connection, handle.StraightIndex, -4.0, out var error))
        {
            throw new InvalidOperationException($"Neither shift direction fits the route: {error}");
        }
    }

    /// <summary>Routes the symmetric U-turn through the real pipeline (A* + collapse pass),
    /// mirroring <see cref="PinLeadCollapseHardeningTests"/>.</summary>
    private static (WaveguideConnectionManager Manager, WaveguideRouter Router,
        WaveguideConnection Connection, PhysicalPin StartPin, PhysicalPin EndPin) RouteUTurn()
    {
        var router = new WaveguideRouter
        {
            MinBendRadiusMicrometers = Radius,
            MinWaveguideSpacingMicrometers = 2.0,
            UseDiagonalRouting = false,
            // The collapse pass only applies to grid routes, so force the A* pipeline.
            PreferDirectStyledRoutes = false,
        };
        var start = TestComponentFactory.CreatePinlessComponent(0, 0);
        var end = TestComponentFactory.CreatePinlessComponent(0, 275);
        var startPin = TestComponentFactory.CreateRoutingPin(start, 50, 25, 0);
        var endPin = TestComponentFactory.CreateRoutingPin(end, 50, 25, 0);
        // Registered pins carve the persistent pin corridors during the grid rebuild,
        // exactly like every real component.
        start.PhysicalPins.Add(startPin);
        end.PhysicalPins.Add(endPin);
        router.InitializePathfindingGrid(-100, -100, 500, 500, new List<Component> { start, end });

        var manager = new WaveguideConnectionManager(router);
        var connection = new WaveguideConnection
        {
            StartPin = startPin,
            EndPin = endPin,
            BendRadiusMicrometers = Radius,
        };
        manager.AddExistingConnection(connection);
        manager.RecalculateAllTransmissions();
        connection.IsPathValid.ShouldBeTrue();
        return (manager, router, connection, startPin, endPin);
    }

    private static double StartPinLead(RoutedPath path, PhysicalPin startPin)
    {
        var (sx, sy) = startPin.GetAbsolutePosition();
        var firstBend = path.Segments.OfType<BendSegment>().First();
        double dx = firstBend.StartPoint.X - sx;
        double dy = firstBend.StartPoint.Y - sy;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
