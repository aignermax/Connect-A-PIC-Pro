using Xunit;
using Shouldly;
using CAP_Core.Routing.AStarPathfinder;
using CAP_Core.Routing;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Components.Connections;

namespace UnitTests.Routing;

/// <summary>
/// Tests for two-phase pathfinding (Phase 1 quick + Phase 2 extended) in WaveguideRouter and AStarPathfinder.
/// </summary>
public class TwoPhasePathfindingTests
{
    // ─── AStarPathfinder cancellation ───────────────────────────────────────

    [Fact]
    public void FindPath_WithCancelledToken_ReturnsNull()
    {
        // Arrange
        var grid = new PathfindingGrid(0, 0, 500, 500, cellSize: 1.0);
        var costCalc = new RoutingCostCalculator { CellSizeMicrometers = 1.0, MinStraightRunCells = 2 };
        var pathfinder = new AStarPathfinder(grid, costCalc);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // pre-cancelled

        // Act
        var path = pathfinder.FindPath(10, 250, GridDirection.East, 490, 250, GridDirection.East,
                                        cts.Token);

        // Assert – cancelled token should abort immediately
        path.ShouldBeNull();
    }

    [Fact]
    public void FindPath_WithDefaultToken_StillFinds()
    {
        // Arrange – no cancellation, simple straight path
        var grid = new PathfindingGrid(0, 0, 100, 100, cellSize: 1.0);
        var costCalc = new RoutingCostCalculator { CellSizeMicrometers = 1.0, MinStraightRunCells = 2 };
        var pathfinder = new AStarPathfinder(grid, costCalc);

        // Act
        var path = pathfinder.FindPath(5, 50, GridDirection.East, 90, 50, GridDirection.East);

        // Assert
        path.ShouldNotBeNull();
        path.Count.ShouldBeGreaterThan(2);
    }

    // ─── WaveguideRouter two-phase configuration ─────────────────────────────

    [Fact]
    public void WaveguideRouter_DefaultPhaseSettings_AreReasonable()
    {
        var router = new WaveguideRouter();

        // Phase 1 should be a meaningful subset of Phase 2
        router.Phase1MaxNodes.ShouldBeGreaterThan(0);
        router.Phase2MaxNodes.ShouldBeGreaterThan(router.Phase1MaxNodes);
    }

    [Fact]
    public void WaveguideRouter_OnComplexRouteStarted_DefaultsToNull()
    {
        var router = new WaveguideRouter();
        router.OnComplexRouteStarted.ShouldBeNull();
    }

    // ─── Two-phase escalation via WaveguideConnectionManager ────────────────

    [Fact]
    public void OnComplexRouteStarted_FiredWhenPhase1NodeBudgetExhausted()
    {
        // Arrange: set Phase1MaxNodes very low so even a simple route triggers Phase 2
        var router = new WaveguideRouter
        {
            Phase1MaxNodes = 1,     // exhausted on first node
            Phase2MaxNodes = 500_000,
            // Force the grid search — a direct styled route would never reach Phase 2.
            PreferDirectStyledRoutes = false,
        };
        router.InitializePathfindingGrid(-100, -100, 5100, 5100,
                                          Enumerable.Empty<Component>());

        bool phase2Fired = false;
        router.OnComplexRouteStarted = () => phase2Fired = true;

        var manager = new WaveguideConnectionManager(router);

        var startPin = CreatePin(100, 100, 0);
        var endPin = CreatePin(200, 100, 180);
        manager.AddConnectionDeferred(startPin, endPin);

        // Act
        manager.OnComplexRouteStarted = () => phase2Fired = true;
        manager.RecalculateAllTransmissions();

        // Assert – Phase 2 must have been triggered
        phase2Fired.ShouldBeTrue("Phase 2 should fire when Phase 1 node budget is exhausted");
    }

    [Fact]
    public void Phase2_CancelledByOuterToken_RoutingAborts()
    {
        // Arrange: set Phase1MaxNodes low to force Phase 2, then cancel before Phase 2 finishes
        var router = new WaveguideRouter
        {
            Phase1MaxNodes = 1,
            Phase2MaxNodes = 10_000_000,
            // Force the grid search — a direct styled route would never reach Phase 2.
            PreferDirectStyledRoutes = false,
        };
        router.InitializePathfindingGrid(-100, -100, 5100, 5100,
                                          Enumerable.Empty<Component>());

        using var cts = new CancellationTokenSource();

        // Cancel as soon as Phase 2 starts
        router.OnComplexRouteStarted = () => cts.Cancel();

        var manager = new WaveguideConnectionManager(router);
        manager.OnComplexRouteStarted = router.OnComplexRouteStarted;

        var startPin = CreatePin(100, 100, 0);
        var endPin = CreatePin(2000, 100, 180);
        manager.AddConnectionDeferred(startPin, endPin);

        // Act – should not hang; cancellation should abort Phase 2 promptly
        manager.RecalculateAllTransmissions(cancellationToken: cts.Token);

        // Assert – routing was cancelled, so no valid path expected
        // (we just verify it returns without deadlock/exception)
        // The connection may have no path or a fallback path — both are acceptable
    }

    [Fact]
    public void SimpleRoute_Phase1Succeeds_Phase2CallbackNotFired()
    {
        // Arrange: enough Phase1 budget for a trivial connection
        var router = new WaveguideRouter { Phase1MaxNodes = 500_000 };
        router.InitializePathfindingGrid(-100, -100, 5100, 5100,
                                          Enumerable.Empty<Component>());

        bool phase2Fired = false;
        var manager = new WaveguideConnectionManager(router);
        manager.OnComplexRouteStarted = () => phase2Fired = true;

        var startPin = CreatePin(100, 100, 0);
        var endPin = CreatePin(200, 100, 180);
        manager.AddConnectionDeferred(startPin, endPin);

        // Act
        manager.RecalculateAllTransmissions();

        // Assert – Phase 2 should NOT be needed for a trivial route
        phase2Fired.ShouldBeFalse("Phase 2 should not fire when Phase 1 finds the path");
    }

    // ─── RoutingOrchestrator status text (integration) ───────────────────────

    [Fact]
    public void WaveguideConnectionManager_OnComplexRouteStarted_Assignable()
    {
        var router = new WaveguideRouter();
        var manager = new WaveguideConnectionManager(router);

        bool called = false;
        manager.OnComplexRouteStarted = () => called = true;

        // Verify round-trip
        manager.OnComplexRouteStarted.ShouldNotBeNull();
        manager.OnComplexRouteStarted!.Invoke();
        called.ShouldBeTrue();
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static PhysicalPin CreatePin(double x, double y, double angleDeg)
    {
        var component = new Component(
            new Dictionary<int, CAP_Core.LightCalculation.SMatrix>(),
            new List<Slider>(),
            "test",
            "",
            new Part[1, 1],
            0,
            "test",
            DiscreteRotation.R0);

        component.PhysicalX = x;
        component.PhysicalY = y;
        component.WidthMicrometers = 10;
        component.HeightMicrometers = 10;

        return new PhysicalPin
        {
            Name = "test",
            OffsetXMicrometers = 0,
            OffsetYMicrometers = 0,
            AngleDegrees = angleDeg,
            ParentComponent = component
        };
    }
}
