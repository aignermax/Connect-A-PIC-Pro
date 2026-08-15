using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Routing;
using CAP_Core.Routing.AStarPathfinder;
using CAP_Core.Routing.InterconnectRouting.SegmentShift;
using Shouldly;
using Xunit;

namespace UnitTests.Routing;

/// <summary>
/// Ownership-aware grid cell state (issue #801): a neighbouring component's PADDING band that
/// covers a FOREIGN pin corridor must not read as a component collision for the route that owns
/// the pin — previously this unfroze manually edited routes (discarding the user's edit) and
/// raised false design issues during shift drags. A foreign component BODY inside the corridor
/// still blocks.
/// </summary>
public class GridCellOwnershipTests
{
    private const double Radius = 10.0;
    private const double Padding = 5.0;

    // ---- Grid-level predicate contract -------------------------------------------------

    [Fact]
    public void ForeignPaddingOverPinCorridor_IsToleratedForOwnPin_ButStillBlockedGenerally()
    {
        var (grid, pin) = CreateOwnerGrid();
        // Body y ∈ [0,20] sits above the corridor; its 5 µm padding reaches y = 25,
        // re-marking corridor cells right where a collapsed bend hugs the pin.
        grid.AddComponentObstacle(TestComponentFactory.CreatePinlessComponent(52, 0, 18, 20));

        var (gx, gy) = grid.PhysicalToGrid(53, 25.5);
        grid.IsBlockedByComponent(gx, gy).ShouldBeTrue(
            "the raster itself is unchanged — the padding band still marks the cell");
        grid.IsBlockedByComponentForRoute(gx, gy, new[] { pin }).ShouldBeFalse(
            "padding-only cells inside the route's own pin corridor are tolerated");
    }

    [Fact]
    public void ForeignBodyInsidePinCorridor_StillBlocks_EvenWithOwnPinTolerance()
    {
        var (grid, pin) = CreateOwnerGrid();
        grid.AddComponentObstacle(TestComponentFactory.CreatePinlessComponent(53, 23, 17, 4));

        var (gx, gy) = grid.PhysicalToGrid(60, 25);
        grid.IsBlockedByComponentForRoute(gx, gy, new[] { pin }).ShouldBeTrue(
            "a foreign component BODY inside the corridor is a real collision");
    }

    [Fact]
    public void Tolerance_IsIndependentOfRegistrationOrder()
    {
        // Neighbour registered BEFORE the pin owner: its padding rasterized while no
        // corridor existed yet — the persistent ownership data must still exempt the cell.
        var grid = new PathfindingGrid(-100, -100, 500, 500, 1.0, Padding);
        grid.AddComponentObstacle(TestComponentFactory.CreatePinlessComponent(52, 0, 18, 20));
        var owner = TestComponentFactory.CreatePinlessComponent(0, 0);
        var pin = TestComponentFactory.CreateRoutingPin(owner, 50, 25, 0);
        owner.PhysicalPins.Add(pin);
        grid.AddComponentObstacle(owner);

        var (gx, gy) = grid.PhysicalToGrid(53, 25.5);
        grid.IsBlockedByComponentForRoute(gx, gy, new[] { pin }).ShouldBeFalse();
    }

    [Fact]
    public void RemovingThePinOwner_RemovesTheCorridorExemption()
    {
        var (grid, pin) = CreateOwnerGrid();
        grid.AddComponentObstacle(TestComponentFactory.CreatePinlessComponent(52, 0, 18, 20));
        grid.RemoveComponentObstacle(pin.ParentComponent!);

        var (gx, gy) = grid.PhysicalToGrid(53, 25.5);
        grid.IsBlockedByComponentForRoute(gx, gy, new[] { pin }).ShouldBeTrue(
            "without a registered corridor the padding cell blocks like any other");
    }

    // ---- End-to-end regressions ---------------------------------------------------------

    /// <summary>
    /// The #795/#792 regression: a collapsed route is frozen by a manual segment shift; a
    /// neighbour is then placed so close to the pin that its padding band covers the pin
    /// corridor. The frozen route must survive the next recalculation — unfreezing would
    /// silently discard the user's shift offsets.
    /// </summary>
    [Fact]
    public void CollapsedFrozenRoute_SurvivesNeighbourPaddingOverItsPinCorridor()
    {
        var (manager, router, connection) = RouteCollapsedUTurn();
        ShiftFirstShiftableStraight(connection);
        connection.IsRouteFrozen.ShouldBeTrue("precondition: the manual shift freezes the route");
        var frozenPath = connection.RoutedPath!;
        var offsets = new Dictionary<int, double>(connection.StraightShiftOffsets);

        router.AddComponentObstacle(TestComponentFactory.CreatePinlessComponent(52, 0, 18, 20));
        manager.RecalculateAllTransmissions();

        connection.IsRouteFrozen.ShouldBeTrue(
            "a neighbour's padding over the own pin corridor is no collision — the edit must survive");
        connection.RoutedPath.ShouldBeSameAs(frozenPath);
        connection.StraightShiftOffsets.Count.ShouldBe(offsets.Count);
        foreach (var (index, offset) in offsets)
            connection.StraightShiftOffsets[index].ShouldBe(offset, 1e-9);
    }

    /// <summary>
    /// Shift-drag refresh on the same layout: the collapsed pin hug under the neighbour's
    /// padding must not raise a false <see cref="RoutedPath.PassesThroughComponent"/> flag.
    /// </summary>
    [Fact]
    public void ShiftDrag_WithNeighbourPaddingOverPinCorridor_RaisesNoFalseCollision()
    {
        var (_, router, connection) = RouteCollapsedUTurn();
        ShiftFirstShiftableStraight(connection);
        router.AddComponentObstacle(TestComponentFactory.CreatePinlessComponent(52, 0, 18, 20));

        SegmentShiftEditor.RefreshComponentCollision(connection, router);

        connection.RoutedPath!.PassesThroughComponent.ShouldBeFalse(
            "padding-only corridor cells must not flag a design issue during a shift drag");
    }

    /// <summary>
    /// Counter-case: a neighbour whose BODY lands inside the corridor (on the collapsed bend)
    /// is a real collision — the frozen route must unfreeze and discard the manual edit.
    /// </summary>
    [Fact]
    public void CollapsedFrozenRoute_StillUnfreezes_WhenNeighbourBodyCoversItsPinCorridor()
    {
        var (manager, router, connection) = RouteCollapsedUTurn();
        ShiftFirstShiftableStraight(connection);
        connection.IsRouteFrozen.ShouldBeTrue("precondition: the manual shift freezes the route");

        router.AddComponentObstacle(TestComponentFactory.CreatePinlessComponent(53, 23, 17, 4));
        manager.RecalculateAllTransmissions();

        connection.IsRouteFrozen.ShouldBeFalse(
            "a foreign body over the pin corridor is a real collision and must unfreeze the route");
        connection.StraightShiftOffsets.ShouldBeEmpty("the manual edit is discarded like a bend override");
    }

    // ---- Fixtures -----------------------------------------------------------------------

    /// <summary>Grid with one pin-owning component: pinless 50×50 at origin, east pin at (50,25).</summary>
    private static (PathfindingGrid Grid, PhysicalPin Pin) CreateOwnerGrid()
    {
        var grid = new PathfindingGrid(-100, -100, 500, 500, 1.0, Padding);
        var owner = TestComponentFactory.CreatePinlessComponent(0, 0);
        var pin = TestComponentFactory.CreateRoutingPin(owner, 50, 25, 0);
        owner.PhysicalPins.Add(pin);
        grid.AddComponentObstacle(owner);
        return (grid, pin);
    }

    /// <summary>
    /// Routes the symmetric U-turn through the real pipeline (A* + collapse pass), yielding a
    /// route whose departure bend hugs the start pin inside the persistent pin corridor
    /// (mirrors <see cref="PinLeadCollapseHardeningTests"/>).
    /// </summary>
    private static (WaveguideConnectionManager Manager, WaveguideRouter Router,
        WaveguideConnection Connection) RouteCollapsedUTurn()
    {
        var router = new WaveguideRouter
        {
            MinBendRadiusMicrometers = Radius,
            MinWaveguideSpacingMicrometers = 2.0,
            UseDiagonalRouting = false,
            PreferDirectStyledRoutes = false,
        };
        var start = TestComponentFactory.CreatePinlessComponent(0, 0);
        var end = TestComponentFactory.CreatePinlessComponent(0, 275);
        var startPin = TestComponentFactory.CreateRoutingPin(start, 50, 25, 0);
        var endPin = TestComponentFactory.CreateRoutingPin(end, 50, 25, 0);
        start.PhysicalPins.Add(startPin);
        end.PhysicalPins.Add(endPin);
        router.InitializePathfindingGrid(-100, -100, 500, 500, new[] { start, end });

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

        var (sx, sy) = startPin.GetAbsolutePosition();
        var firstBend = connection.RoutedPath!.Segments.OfType<BendSegment>().First();
        double lead = Math.Sqrt(
            Math.Pow(firstBend.StartPoint.X - sx, 2) + Math.Pow(firstBend.StartPoint.Y - sy, 2));
        lead.ShouldBe(0, 1e-3, "precondition: the departure lead is collapsed onto the pin");
        return (manager, router, connection);
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
}
