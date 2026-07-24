using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Components.Connections;
using CAP_Core.Components.FormulaReading;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Routing;

/// <summary>
/// Tests for async routing infrastructure (deferred connections, async recalculation, cancellation).
/// </summary>
[Trait("Category", "Slow")]
public class AsyncRoutingTests
{
    [Fact]
    public async Task RecalculateAllTransmissionsAsync_CompletesSuccessfully()
    {
        var router = new WaveguideRouter();
        var manager = new WaveguideConnectionManager(router);
        var startComp = CreateTestComponent(0, 0);
        var endComp = CreateTestComponent(200, 0);
        var startPin = CreateOutputPin(startComp);
        var endPin = CreateInputPin(endComp);

        router.InitializePathfindingGrid(-50, -50, 300, 100,
            new[] { startComp, endComp });

        manager.AddConnectionDeferred(startPin, endPin);

        var result = await manager.RecalculateAllTransmissionsAsync();

        result.ShouldBeTrue();
        manager.Connections.Count.ShouldBe(1);
    }

    [Fact]
    public async Task RecalculateAllTransmissionsAsync_Cancellation_ReturnsFalse()
    {
        var manager = new WaveguideConnectionManager(new WaveguideRouter());
        var startComp = CreateTestComponent(0, 0);
        var endComp = CreateTestComponent(200, 0);
        var startPin = CreateOutputPin(startComp);
        var endPin = CreateInputPin(endComp);

        manager.AddConnectionDeferred(startPin, endPin);

        var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        var result = await manager.RecalculateAllTransmissionsAsync(null, cts.Token);
        result.ShouldBeFalse();
    }

    [Fact]
    public void AddConnectionDeferred_DoesNotRoute()
    {
        var manager = new WaveguideConnectionManager(new WaveguideRouter());
        var startComp = CreateTestComponent(0, 0);
        var endComp = CreateTestComponent(200, 0);
        var startPin = CreateOutputPin(startComp);
        var endPin = CreateInputPin(endComp);

        var connection = manager.AddConnectionDeferred(startPin, endPin);

        connection.ShouldNotBeNull();
        manager.Connections.Count.ShouldBe(1);
        // Path should be null because no routing was performed
        connection.RoutedPath.ShouldBeNull();
        connection.IsPathValid.ShouldBeFalse();
    }

    [Fact]
    public void RemoveConnectionDeferred_DoesNotRecalculate()
    {
        var manager = new WaveguideConnectionManager(new WaveguideRouter());
        var startComp = CreateTestComponent(0, 0);
        var endComp = CreateTestComponent(200, 0);

        var conn1 = manager.AddConnectionDeferred(
            CreateOutputPin(startComp), CreateInputPin(endComp));

        var startComp2 = CreateTestComponent(0, 100);
        var endComp2 = CreateTestComponent(200, 100);
        var conn2 = manager.AddConnectionDeferred(
            CreateOutputPin(startComp2), CreateInputPin(endComp2));

        manager.Connections.Count.ShouldBe(2);

        manager.RemoveConnectionDeferred(conn1);

        manager.Connections.Count.ShouldBe(1);
        // conn2 should still have no route (deferred removal doesn't trigger recalculation)
        conn2.RoutedPath.ShouldBeNull();
    }

    [Fact]
    public void RemoveConnectionsForComponent_Deferred_RemovesCorrectConnections()
    {
        var manager = new WaveguideConnectionManager(new WaveguideRouter());
        var compA = CreateTestComponent(0, 0);
        var compB = CreateTestComponent(200, 0);
        var compC = CreateTestComponent(400, 0);

        manager.AddConnectionDeferred(CreateOutputPin(compA), CreateInputPin(compB));
        manager.AddConnectionDeferred(CreateOutputPin(compB), CreateInputPin(compC));

        manager.Connections.Count.ShouldBe(2);

        manager.RemoveConnectionsForComponent(compB);

        manager.Connections.Count.ShouldBe(0);
    }

    [Fact]
    public async Task RecalculateAllTransmissionsAsync_AfterDeferred_RoutesSuccessfully()
    {
        var router = new WaveguideRouter();
        var manager = new WaveguideConnectionManager(router);
        var startComp = CreateTestComponent(0, 0);
        var endComp = CreateTestComponent(200, 0);
        var startPin = CreateOutputPin(startComp);
        var endPin = CreateInputPin(endComp);

        router.InitializePathfindingGrid(-50, -50, 300, 100,
            new[] { startComp, endComp });

        var connection = manager.AddConnectionDeferred(startPin, endPin);

        // Before async recalculation: no route
        connection.RoutedPath.ShouldBeNull();

        await manager.RecalculateAllTransmissionsAsync();

        // After async recalculation: route should be computed
        connection.RoutedPath.ShouldNotBeNull();
        connection.IsPathValid.ShouldBeTrue();
        connection.PathLengthMicrometers.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task ConcurrentRecalculations_DoNotCorruptState()
    {
        // Simulates the crash scenario: multiple overlapping routing operations
        // Previously caused InvalidOperationException on Dictionary concurrent access
        var router = new WaveguideRouter();
        var manager = new WaveguideConnectionManager(router);
        var startComp = CreateTestComponent(0, 0);
        var endComp = CreateTestComponent(200, 0);
        var startPin = CreateOutputPin(startComp);
        var endPin = CreateInputPin(endComp);

        router.InitializePathfindingGrid(-50, -50, 300, 100,
            new[] { startComp, endComp });

        manager.AddConnectionDeferred(startPin, endPin);

        // Fire multiple concurrent routing operations (simulates rapid UI changes)
        var tasks = new List<Task>();
        for (int i = 0; i < 5; i++)
        {
            tasks.Add(manager.RecalculateAllTransmissionsAsync());
        }

        // All should complete without InvalidOperationException
        await Task.WhenAll(tasks);

        manager.Connections.Count.ShouldBe(1);
    }

    [Fact]
    public async Task CancelAndRestart_DoesNotThrowConcurrencyException()
    {
        // Simulates the exact user scenario: start routing, cancel, restart immediately
        var router = new WaveguideRouter();
        var manager = new WaveguideConnectionManager(router);
        var startComp = CreateTestComponent(0, 0);
        var endComp = CreateTestComponent(200, 0);
        var startPin = CreateOutputPin(startComp);
        var endPin = CreateInputPin(endComp);

        router.InitializePathfindingGrid(-50, -50, 300, 100,
            new[] { startComp, endComp });

        manager.AddConnectionDeferred(startPin, endPin);

        // Start first routing
        var cts1 = new CancellationTokenSource();
        var task1 = manager.RecalculateAllTransmissionsAsync(null, cts1.Token);

        // Cancel immediately and start second routing (overlapping)
        cts1.Cancel();
        var task2 = manager.RecalculateAllTransmissionsAsync();

        // Both should complete without crashing (no exceptions thrown)
        await task1;
        await task2;

        manager.Connections.Count.ShouldBe(1);
    }

    [Fact]
    public async Task RapidCancelRestart_StressTest()
    {
        // Stress test: rapidly cancel and restart routing 10 times
        var router = new WaveguideRouter();
        var manager = new WaveguideConnectionManager(router);
        var startComp = CreateTestComponent(0, 0);
        var endComp = CreateTestComponent(200, 0);

        router.InitializePathfindingGrid(-50, -50, 300, 100,
            new[] { startComp, endComp });

        manager.AddConnectionDeferred(CreateOutputPin(startComp), CreateInputPin(endComp));

        var previousTasks = new List<Task>();

        for (int i = 0; i < 10; i++)
        {
            var cts = new CancellationTokenSource();
            var task = manager.RecalculateAllTransmissionsAsync(null, cts.Token);
            previousTasks.Add(task);

            // Cancel after a tiny delay to overlap with execution
            if (i < 9) cts.Cancel();
        }

        // Wait for all — none should throw any exception
        await Task.WhenAll(previousTasks);

        manager.Connections.Count.ShouldBe(1);
    }

    [Fact]
    public void IncrementalRouting_PreservesValidRoutes()
    {
        // Setup: two connections, both routed successfully
        var router = new WaveguideRouter();
        var manager = new WaveguideConnectionManager(router);
        var compA = CreateTestComponent(0, 0);
        var compB = CreateTestComponent(200, 0);
        var compC = CreateTestComponent(0, 100);
        var compD = CreateTestComponent(200, 100);

        router.MinBendRadiusMicrometers = 10.0;
        router.AStarCellSize = 2.0;
        router.CostCalculator.MinStraightRunCells = 20;
        router.CostCalculator.MinPinEscapeCells = 15;
        router.InitializePathfindingGrid(-50, -50, 300, 200,
            new[] { compA, compB, compC, compD });

        var conn1 = manager.AddConnectionDeferred(CreateOutputPin(compA), CreateInputPin(compB));
        var conn2 = manager.AddConnectionDeferred(CreateOutputPin(compC), CreateInputPin(compD));

        // Route all connections — first pass may fix invalid routes from stale shared state
        manager.RecalculateAllTransmissions();
        conn1.IsPathValid.ShouldBeTrue();
        conn2.IsPathValid.ShouldBeTrue();

        // Route again to reach steady state (incremental routing may fix first-pass artifacts)
        manager.RecalculateAllTransmissions();
        conn1.IsPathValid.ShouldBeTrue();
        conn2.IsPathValid.ShouldBeTrue();

        // Store the steady-state paths
        var conn1PathLength = conn1.PathLengthMicrometers;
        var conn2PathLength = conn2.PathLengthMicrometers;

        // Third routing — should preserve steady-state paths exactly
        manager.RecalculateAllTransmissions();

        conn1.PathLengthMicrometers.ShouldBe(conn1PathLength);
        conn2.PathLengthMicrometers.ShouldBe(conn2PathLength);
    }

    [Fact]
    public void IncrementalRouting_ReroutesOnlyAffectedConnections()
    {
        // Setup: two connections routed successfully
        var router = new WaveguideRouter();
        var manager = new WaveguideConnectionManager(router);
        var compA = CreateTestComponent(0, 0);
        var compB = CreateTestComponent(200, 0);
        var compC = CreateTestComponent(0, 100);
        var compD = CreateTestComponent(200, 100);

        router.MinBendRadiusMicrometers = 10.0;
        router.AStarCellSize = 2.0;
        router.CostCalculator.MinStraightRunCells = 20;
        router.CostCalculator.MinPinEscapeCells = 15;
        router.InitializePathfindingGrid(-50, -50, 300, 200,
            new[] { compA, compB, compC, compD });

        var conn1 = manager.AddConnectionDeferred(CreateOutputPin(compA), CreateInputPin(compB));
        var conn2 = manager.AddConnectionDeferred(CreateOutputPin(compC), CreateInputPin(compD));

        manager.RecalculateAllTransmissions();

        conn1.IsPathValid.ShouldBeTrue();
        conn2.IsPathValid.ShouldBeTrue();

        var conn2PathLength = conn2.PathLengthMicrometers;

        // Move compA — conn1 becomes invalid, conn2 should be unaffected
        compA.PhysicalX = 0;
        compA.PhysicalY = 50; // Move down
        router.PathfindingGrid!.RebuildFromComponents(new[] { compA, compB, compC, compD });

        manager.RecalculateAllTransmissions();

        // conn2 should keep its original path (not affected by compA move)
        conn2.PathLengthMicrometers.ShouldBe(conn2PathLength);
        // conn1 should have been re-routed (endpoint moved)
        conn1.IsPathValid.ShouldBeTrue();
    }

    private static Component CreateTestComponent(double x, double y)
    {
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin>());
        var component = new Component(
            new Dictionary<int, SMatrix>(), new List<Slider>(),
            "test", "", parts, 0,
            $"Test_{x}_{y}", DiscreteRotation.R0);
        component.WidthMicrometers = 50;
        component.HeightMicrometers = 50;
        component.PhysicalX = x;
        component.PhysicalY = y;
        return component;
    }

    private static PhysicalPin CreateOutputPin(Component c) => new()
    {
        Name = "output",
        OffsetXMicrometers = c.WidthMicrometers,
        OffsetYMicrometers = c.HeightMicrometers / 2,
        AngleDegrees = 0,
        ParentComponent = c
    };

    private static PhysicalPin CreateInputPin(Component c) => new()
    {
        Name = "input",
        OffsetXMicrometers = 0,
        OffsetYMicrometers = c.HeightMicrometers / 2,
        AngleDegrees = 180,
        ParentComponent = c
    };
}
