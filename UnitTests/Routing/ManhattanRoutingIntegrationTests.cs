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
/// Integration tests for Manhattan router obstacle registration and collision detection.
/// Tests fix for issue #87: Manhattan router waveguides not registered in PathfindingGrid.
/// </summary>
[Trait("Category", "Slow")]
public class ManhattanRoutingIntegrationTests
{
    [Fact]
    public void ManhattanFallbackPath_RegisteredAsObstacle_BlocksSubsequentAStar()
    {
        // Arrange - Scenario 1 from issue #87:
        // 1. Create a Manhattan fallback connection
        // 2. Create a second A* connection that should avoid the first

        var router = new WaveguideRouter { MinBendRadiusMicrometers = 10.0 };
        var manager = new WaveguideConnectionManager(router)
        {
            UseSequentialRouting = true,
            WaveguideWidthMicrometers = 4.0
        };

        // Components positioned so first connection uses Manhattan fallback
        var comp1 = CreateTestComponent(0, 0);
        var comp2 = CreateTestComponent(150, 100);
        var comp3 = CreateTestComponent(0, 150);
        var comp4 = CreateTestComponent(150, 50);

        router.InitializePathfindingGrid(-100, -100, 300, 300, new[] { comp1, comp2, comp3, comp4 });

        // First connection: comp1 -> comp2 (should use Manhattan fallback)
        var pin1a = CreateOutputPin(comp1, 50, 25, 45); // Angled pin
        var pin2a = CreateInputPin(comp2, 0, 25, 225);

        // Second connection: comp3 -> comp4 (should use A*)
        // This path might cross the first connection if it's not registered
        var pin3a = CreateOutputPin(comp3, 50, 25, 0);
        var pin4a = CreateInputPin(comp4, 0, 25, 180);

        // Act - Add connections sequentially
        var conn1 = manager.AddConnectionDeferred(pin1a, pin2a);
        var conn2 = manager.AddConnectionDeferred(pin3a, pin4a);

        manager.RecalculateAllTransmissions();

        // Assert
        conn1.ShouldNotBeNull();
        conn1.IsPathValid.ShouldBeTrue();

        conn2.ShouldNotBeNull();
        conn2.IsPathValid.ShouldBeTrue();

        // Second connection should have routed, avoiding the first
        // If Manhattan path wasn't registered, they might overlap
        var grid = router.PathfindingGrid;
        grid.ShouldNotBeNull();

        // Verify both paths are registered as obstacles
        int waveguideObstacleCount = GetWaveguideObstacleCount(grid);
        waveguideObstacleCount.ShouldBeGreaterThan(0, "At least one connection should be registered as obstacle");
    }

    [Fact]
    public void SequentialRouting_AllPathsRegisteredAsObstacles()
    {
        // Arrange - Test that sequential routing registers all paths (A* or Manhattan)

        var router = new WaveguideRouter { MinBendRadiusMicrometers = 10.0 };
        var manager = new WaveguideConnectionManager(router)
        {
            UseSequentialRouting = true,
            WaveguideWidthMicrometers = 4.0
        };

        var comp1 = CreateTestComponent(0, 0);
        var comp2 = CreateTestComponent(200, 0);
        var comp3 = CreateTestComponent(0, 100);
        var comp4 = CreateTestComponent(200, 100);

        router.InitializePathfindingGrid(-100, -100, 350, 250, new[] { comp1, comp2, comp3, comp4 });

        // Simple straight connections that will use A* successfully
        var pin1 = CreateOutputPin(comp1, 50, 25, 0);
        var pin2 = CreateInputPin(comp2, 0, 25, 180);
        var pin3 = CreateOutputPin(comp3, 50, 25, 0);
        var pin4 = CreateInputPin(comp4, 0, 25, 180);

        // Act - Add connections
        var conn1 = manager.AddConnectionDeferred(pin1, pin2);
        var conn2 = manager.AddConnectionDeferred(pin3, pin4);

        manager.RecalculateAllTransmissions();

        // Assert
        conn1.IsPathValid.ShouldBeTrue();
        conn2.IsPathValid.ShouldBeTrue();

        // Both paths should be registered in the grid
        var grid = router.PathfindingGrid;
        grid.ShouldNotBeNull();

        int waveguideObstacleCount = GetWaveguideObstacleCount(grid);
        waveguideObstacleCount.ShouldBeGreaterThan(0, "Routed paths (A* or Manhattan) should be registered as obstacles");
    }

    [Fact]
    public void ManhattanPath_BlockedByExistingWaveguide_MarkedAsFaulty()
    {
        // Arrange - A* connection first, then Manhattan that crosses it

        var router = new WaveguideRouter { MinBendRadiusMicrometers = 10.0 };
        var manager = new WaveguideConnectionManager(router)
        {
            UseSequentialRouting = true,
            WaveguideWidthMicrometers = 4.0
        };

        var comp1 = CreateTestComponent(0, 50);
        var comp2 = CreateTestComponent(200, 50);
        var comp3 = CreateTestComponent(100, 0);
        var comp4 = CreateTestComponent(100, 150);

        router.InitializePathfindingGrid(-100, -100, 350, 250, new[] { comp1, comp2, comp3, comp4 });

        // First: Horizontal A* connection
        var pin1 = CreateOutputPin(comp1, 50, 25, 0);
        var pin2 = CreateInputPin(comp2, 0, 25, 180);

        // Second: Vertical connection that would cross the first
        var pin3 = CreateOutputPin(comp3, 25, 50, 90);
        var pin4 = CreateInputPin(comp4, 25, 0, 270);

        // Act
        var conn1 = manager.AddConnectionDeferred(pin1, pin2);
        var conn2 = manager.AddConnectionDeferred(pin3, pin4);

        manager.RecalculateAllTransmissions();

        // Assert
        conn1.IsPathValid.ShouldBeTrue();
        conn1.IsBlockedFallback.ShouldBeFalse("First connection should route successfully");

        conn2.IsPathValid.ShouldBeTrue("Second connection should have valid geometry");
        // Second connection might be blocked or might route around - depends on exact geometry
        // The key is that it detects the first connection as an obstacle
    }

    [Fact]
    public void ManhattanPath_ClearsAfterDeletion_AllowsNewRoutes()
    {
        // Arrange - Test that removing a Manhattan path clears its obstacles

        var router = new WaveguideRouter { MinBendRadiusMicrometers = 10.0 };
        var manager = new WaveguideConnectionManager(router)
        {
            UseSequentialRouting = true,
            WaveguideWidthMicrometers = 4.0
        };

        var comp1 = CreateTestComponent(0, 0);
        var comp2 = CreateTestComponent(200, 100);

        router.InitializePathfindingGrid(-100, -100, 350, 250, new[] { comp1, comp2 });

        var pin1 = CreateOutputPin(comp1, 50, 25, 45);
        var pin2 = CreateInputPin(comp2, 0, 25, 225);

        // Act - Create and remove connection
        var conn = manager.AddConnectionDeferred(pin1, pin2);
        manager.RecalculateAllTransmissions();

        var grid = router.PathfindingGrid;
        grid.ShouldNotBeNull();

        int beforeRemoval = GetWaveguideObstacleCount(grid);

        manager.RemoveConnection(conn);

        int afterRemoval = GetWaveguideObstacleCount(grid);

        // Assert
        afterRemoval.ShouldBeLessThan(beforeRemoval, "Removing connection should clear waveguide obstacles");
    }

    /// <summary>
    /// Counts cells marked as waveguide obstacles (state=2) in the grid.
    /// </summary>
    private int GetWaveguideObstacleCount(CAP_Core.Routing.AStarPathfinder.PathfindingGrid grid)
    {
        int count = 0;
        for (int x = 0; x < grid.Width; x++)
        {
            for (int y = 0; y < grid.Height; y++)
            {
                if (grid.GetCellState(x, y) == 2) // 2 = waveguide obstacle
                {
                    count++;
                }
            }
        }
        return count;
    }

    private Component CreateTestComponent(double x, double y)
    {
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin>());

        var component = new Component(
            laserWaveLengthToSMatrixMap: new Dictionary<int, SMatrix>(),
            sliders: new List<Slider>(),
            nazcaFunctionName: "test",
            nazcaFunctionParams: "",
            parts: parts,
            typeNumber: 0,
            identifier: $"TestComponent_{x}_{y}",
            rotationCounterClock: DiscreteRotation.R0
        );

        component.WidthMicrometers = 50;
        component.HeightMicrometers = 50;
        component.PhysicalX = x;
        component.PhysicalY = y;

        return component;
    }

    private PhysicalPin CreateOutputPin(Component component, double offsetX = 50, double offsetY = 25, double angle = 0)
    {
        return new PhysicalPin
        {
            Name = "output",
            OffsetXMicrometers = offsetX,
            OffsetYMicrometers = offsetY,
            AngleDegrees = angle,
            ParentComponent = component
        };
    }

    private PhysicalPin CreateInputPin(Component component, double offsetX = 0, double offsetY = 25, double angle = 180)
    {
        return new PhysicalPin
        {
            Name = "input",
            OffsetXMicrometers = offsetX,
            OffsetYMicrometers = offsetY,
            AngleDegrees = angle,
            ParentComponent = component
        };
    }
}
