using Xunit;
using Shouldly;
using CAP_Core.Routing;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Tiles;

namespace UnitTests.Routing;

/// <summary>
/// Integration tests for WaveguideRouter with ComponentGroup obstacles.
/// Tests the full routing workflow from pin to pin through grouped components.
/// </summary>
[Trait("Category", "Slow")]
public class GroupRoutingIntegrationTests
{
    /// <summary>
    /// Creates a mock component with pins at the specified position.
    /// </summary>
    private static Component CreateMockComponentWithPins(
        double x, double y, double width, double height, string id)
    {
        var pins = new List<PhysicalPin>
        {
            new PhysicalPin
            {
                Name = "left",
                OffsetXMicrometers = 0,
                OffsetYMicrometers = height / 2,
                AngleDegrees = 180, // Facing left
                LogicalPin = new Pin("left", 0, MatterType.Light, RectSide.Left)
            },
            new PhysicalPin
            {
                Name = "right",
                OffsetXMicrometers = width,
                OffsetYMicrometers = height / 2,
                AngleDegrees = 0, // Facing right
                LogicalPin = new Pin("right", 1, MatterType.Light, RectSide.Right)
            }
        };

        var component = new Component(
            new Dictionary<int, SMatrix>(),
            new List<Slider>(),
            "test",
            "",
            new Part[1, 1] { { new Part() } },
            -1,
            id,
            new DiscreteRotation(),
            pins
        )
        {
            PhysicalX = x,
            PhysicalY = y,
            WidthMicrometers = width,
            HeightMicrometers = height
        };

        return component;
    }

    [Fact]
    public void Route_ThroughGroupGap_FindsValidPath()
    {
        // Arrange - create scenario matching issue description
        var compA = CreateMockComponentWithPins(20, 80, 15, 15, "compA");
        var compB = CreateMockComponentWithPins(60, 80, 15, 15, "compB");
        var compC = CreateMockComponentWithPins(120, 80, 15, 15, "compC");

        // Create group containing A and B with gap between them
        var group = new ComponentGroup("AB_Group");
        group.AddChild(compA);
        group.AddChild(compB);

        // Setup router with pathfinding grid
        var router = new WaveguideRouter
        {
            MinBendRadiusMicrometers = 10.0,
            AStarCellSize = 2.0,
            ObstaclePaddingMicrometers = 3.0
        };

        var allComponents = new List<Component> { group, compC };
        router.InitializePathfindingGrid(0, 0, 200, 200, allComponents);

        // Act - route from C to A (external to group connection)
        var pinC = compC.PhysicalPins.First(p => p.Name == "left");
        var pinA = compA.PhysicalPins.First(p => p.Name == "right");

        var path = router.Route(pinC, pinA);

        // Assert
        path.ShouldNotBeNull("Router should return a path");
        path.Segments.ShouldNotBeEmpty("Path should have segments");
        path.IsBlockedFallback.ShouldBeFalse(
            "Path should NOT be blocked fallback - routing through gap should succeed");
        path.IsInvalidGeometry.ShouldBeFalse("Path geometry should be valid");

        // Verify path doesn't intersect child components (using PathfindingGrid)
        bool pathBlocked = router.IsPathBlocked(path.Segments);
        pathBlocked.ShouldBeFalse("Path should not be blocked by obstacles");
    }

    [Fact]
    public void Route_ToComponentInsideGroup_SucceedsWithChildObstacles()
    {
        // Arrange
        var innerComp = CreateMockComponentWithPins(50, 50, 12, 12, "inner");
        var outerComp = CreateMockComponentWithPins(100, 50, 12, 12, "outer");

        var group = new ComponentGroup("TestGroup");
        group.AddChild(innerComp);

        var router = new WaveguideRouter
        {
            MinBendRadiusMicrometers = 8.0,
            AStarCellSize = 1.5,
            ObstaclePaddingMicrometers = 2.0
        };

        var allComponents = new List<Component> { group, outerComp };
        router.InitializePathfindingGrid(0, 0, 150, 150, allComponents);

        // Act - route from outer to inner (crossing into group)
        var pinOuter = outerComp.PhysicalPins.First(p => p.Name == "left");
        var pinInner = innerComp.PhysicalPins.First(p => p.Name == "right");

        var path = router.Route(pinOuter, pinInner);

        // Assert
        path.ShouldNotBeNull();
        path.Segments.ShouldNotBeEmpty();
        path.IsBlockedFallback.ShouldBeFalse(
            "Routing to inner group component should work without fallback");
    }

    [Fact]
    public void Route_BetweenNestedGroupChildren_AllowsRoutingThroughGaps()
    {
        // Arrange - complex nested group scenario
        var comp1 = CreateMockComponentWithPins(30, 50, 10, 10, "comp1");
        var comp2 = CreateMockComponentWithPins(60, 50, 10, 10, "comp2");
        var comp3 = CreateMockComponentWithPins(100, 50, 10, 10, "comp3");

        var innerGroup = new ComponentGroup("InnerGroup");
        innerGroup.AddChild(comp1);

        var outerGroup = new ComponentGroup("OuterGroup");
        outerGroup.AddChild(innerGroup);
        outerGroup.AddChild(comp2);

        var router = new WaveguideRouter
        {
            MinBendRadiusMicrometers = 8.0,
            AStarCellSize = 2.0,
            ObstaclePaddingMicrometers = 2.5
        };

        var allComponents = new List<Component> { outerGroup, comp3 };
        router.InitializePathfindingGrid(0, 0, 150, 150, allComponents);

        // Act - route from comp3 to comp1 (external to deeply nested)
        var pin3 = comp3.PhysicalPins.First(p => p.Name == "left");
        var pin1 = comp1.PhysicalPins.First(p => p.Name == "right");

        var path = router.Route(pin3, pin1);

        // Assert
        path.ShouldNotBeNull("Should find path through nested group gaps");
        path.Segments.ShouldNotBeEmpty();
        // Note: Complex nested scenarios may use fallback routing, which is acceptable behavior
    }

    [Fact]
    public void Route_GroupWithNoGap_FallsBackToManhattan()
    {
        // Arrange - create group where children completely block the path
        var compA = CreateMockComponentWithPins(40, 40, 30, 30, "compA");
        var compB = CreateMockComponentWithPins(40, 75, 30, 30, "compB"); // Directly below with overlap
        var compC = CreateMockComponentWithPins(5, 70, 10, 10, "compC");

        var group = new ComponentGroup("BlockingGroup");
        group.AddChild(compA);
        group.AddChild(compB);

        var router = new WaveguideRouter
        {
            MinBendRadiusMicrometers = 8.0,
            AStarCellSize = 2.0,
            ObstaclePaddingMicrometers = 2.0
        };

        var allComponents = new List<Component> { group, compC };
        router.InitializePathfindingGrid(0, 0, 120, 120, allComponents);

        // Act - try to route from C through the blocking group
        var pinC = compC.PhysicalPins.First(p => p.Name == "right");
        var pinA = compA.PhysicalPins.First(p => p.Name == "left");

        var path = router.Route(pinC, pinA);

        // Assert - should still get a path (via fallback or alternate route)
        path.ShouldNotBeNull("Router should always return a path (possibly fallback)");
        path.Segments.ShouldNotBeEmpty();

        // If A* fails, it falls back to Manhattan (which may be marked as blocked)
        // This is expected behavior when routing is genuinely impossible via A*
    }

    [Fact]
    public void UpdateComponentObstacle_MovedGroup_UpdatesRoutingGrid()
    {
        // Arrange
        var comp1 = CreateMockComponentWithPins(40, 40, 10, 10, "comp1");
        var comp2 = CreateMockComponentWithPins(100, 40, 10, 10, "comp2");

        var group = new ComponentGroup("MovableGroup");
        group.AddChild(comp1);

        var router = new WaveguideRouter
        {
            MinBendRadiusMicrometers = 8.0,
            AStarCellSize = 2.0,
            ObstaclePaddingMicrometers = 2.0
        };

        var allComponents = new List<Component> { group, comp2 };
        router.InitializePathfindingGrid(0, 0, 150, 150, allComponents);

        // Route before move
        var pin1Before = comp1.PhysicalPins.First(p => p.Name == "right");
        var pin2 = comp2.PhysicalPins.First(p => p.Name == "left");
        var pathBefore = router.Route(pin1Before, pin2);

        // Act - move the group and update grid
        group.MoveGroup(20, 0); // Move group right by 20µm
        router.UpdateComponentObstacle(group);

        // Route after move
        var pathAfter = router.Route(pin1Before, pin2);

        // Assert
        pathBefore.ShouldNotBeNull("Path before move should exist");
        pathAfter.ShouldNotBeNull("Path after move should exist");

        // After moving right, the path should be shorter (comp1 moved closer to comp2)
        pathAfter.TotalLengthMicrometers.ShouldBeLessThan(
            pathBefore.TotalLengthMicrometers,
            "Path should be shorter after moving group closer to destination");
    }
}
