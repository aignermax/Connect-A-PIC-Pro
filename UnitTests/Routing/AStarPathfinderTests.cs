using Xunit;
using Shouldly;
using CAP_Core.Routing.AStarPathfinder;
using CAP_Core.Routing;
using CAP_Core.Components;
using CAP_Core.Components.Core;

namespace UnitTests.Routing;

public class AStarPathfinderTests
{
    [Fact]
    public void FindPath_StraightLine_FindsPath()
    {
        // Arrange
        var grid = new PathfindingGrid(0, 0, 100, 100, cellSize: 1.0);
        var costCalculator = new RoutingCostCalculator
        {
            CellSizeMicrometers = 1.0,
            MinStraightRunCells = 5
        };
        var pathfinder = new AStarPathfinder(grid, costCalculator);

        // Act - find path from (10,50) going East to (90,50) arriving from West
        var path = pathfinder.FindPath(10, 50, GridDirection.East, 90, 50, GridDirection.East);

        // Assert
        path.ShouldNotBeNull();
        path.Count.ShouldBeGreaterThan(2);
        path[0].X.ShouldBe(10);
        path[0].Y.ShouldBe(50);
    }

    [Fact]
    public void FindPath_WithObstacle_RoutesAround()
    {
        // Arrange - create grid with an obstacle in the middle
        var grid = new PathfindingGrid(0, 0, 100, 100, cellSize: 1.0);

        // Add obstacle from (40,40) to (60,60)
        var obstacle = CreateMockComponent(40, 40, 20, 20);
        grid.AddComponentObstacle(obstacle);

        var costCalculator = new RoutingCostCalculator
        {
            CellSizeMicrometers = 1.0,
            MinStraightRunCells = 5,
            TurnCostPer90Degrees = 10 // Lower turn cost to encourage going around
        };
        var pathfinder = new AStarPathfinder(grid, costCalculator);

        // Act - try to go straight through where the obstacle is
        var path = pathfinder.FindPath(10, 50, GridDirection.East, 90, 50, GridDirection.East);

        // Assert
        path.ShouldNotBeNull();
        path.Count.ShouldBeGreaterThan(2);

        // Path should not go through the obstacle area
        foreach (var node in path)
        {
            bool inObstacle = node.X >= 40 && node.X <= 60 && node.Y >= 40 && node.Y <= 60;
            inObstacle.ShouldBeFalse($"Path goes through obstacle at ({node.X}, {node.Y})");
        }
    }

    [Fact]
    public void FindPath_NoPathExists_ReturnsNull()
    {
        // Arrange - create grid completely blocked
        var grid = new PathfindingGrid(0, 0, 100, 100, cellSize: 1.0);

        // Block a wall across the entire grid
        for (int x = 45; x <= 55; x++)
        {
            for (int y = 0; y < 100; y++)
            {
                // Manually mark as blocked by creating small obstacles
                var obstacle = CreateMockComponent(x, y, 1, 1);
                grid.AddComponentObstacle(obstacle);
            }
        }

        var costCalculator = new RoutingCostCalculator { CellSizeMicrometers = 1.0, MinStraightRunCells = 2 };
        var pathfinder = new AStarPathfinder(grid, costCalculator) { MaxNodesExpanded = 10000 };

        // Act
        var path = pathfinder.FindPath(10, 50, GridDirection.East, 90, 50, GridDirection.East);

        // Assert
        path.ShouldBeNull();
    }

    [Fact]
    public void FindPath_RequiresTurn_TurnsCorrectly()
    {
        // Arrange
        var grid = new PathfindingGrid(0, 0, 100, 100, cellSize: 1.0);
        var costCalculator = new RoutingCostCalculator
        {
            CellSizeMicrometers = 1.0,
            MinStraightRunCells = 5
        };
        var pathfinder = new AStarPathfinder(grid, costCalculator);

        // Act - start going East, but target is to the North
        var path = pathfinder.FindPath(10, 10, GridDirection.East, 50, 80, GridDirection.North);

        // Assert
        path.ShouldNotBeNull();
        path.Count.ShouldBeGreaterThan(2);

        // Verify we have at least one direction change
        bool hasDirectionChange = false;
        for (int i = 1; i < path.Count; i++)
        {
            if (path[i].Direction != path[i - 1].Direction)
            {
                hasDirectionChange = true;
                break;
            }
        }
        hasDirectionChange.ShouldBeTrue("Path should contain at least one turn");
    }

    private static Component CreateMockComponent(double x, double y, double width, double height)
    {
        // Create a minimal component for testing
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
        component.WidthMicrometers = width;
        component.HeightMicrometers = height;

        return component;
    }
}

public class PathfindingGridTests
{
    [Fact]
    public void PhysicalToGrid_ConvertsCorrectly()
    {
        // Arrange
        var grid = new PathfindingGrid(0, 0, 100, 100, cellSize: 2.0);

        // Act
        var (gx, gy) = grid.PhysicalToGrid(50, 30);

        // Assert - at 2µm per cell, 50µm = cell 25, 30µm = cell 15
        gx.ShouldBe(25);
        gy.ShouldBe(15);
    }

    [Fact]
    public void GridToPhysical_ReturnsCenter()
    {
        // Arrange
        var grid = new PathfindingGrid(0, 0, 100, 100, cellSize: 2.0);

        // Act
        var (px, py) = grid.GridToPhysical(25, 15);

        // Assert - cell 25 at 2µm = 50µm, plus 1µm for center = 51µm
        px.ShouldBe(51.0);
        py.ShouldBe(31.0);
    }

    [Fact]
    public void AddComponentObstacle_BlocksCells()
    {
        // Arrange
        var grid = new PathfindingGrid(0, 0, 100, 100, cellSize: 1.0);

        // Act
        var component = CreateMockComponent(40, 40, 20, 20);
        grid.AddComponentObstacle(component);

        // Assert - cells in the component area should be blocked
        grid.IsBlocked(50, 50).ShouldBeTrue();
        grid.IsBlocked(10, 10).ShouldBeFalse();
    }

    [Fact]
    public void RemoveComponentObstacle_UnblocksCells()
    {
        // Arrange
        var grid = new PathfindingGrid(0, 0, 100, 100, cellSize: 1.0);
        var component = CreateMockComponent(40, 40, 20, 20);
        grid.AddComponentObstacle(component);

        // Act
        grid.RemoveComponentObstacle(component);

        // Assert
        grid.IsBlocked(50, 50).ShouldBeFalse();
    }

    [Fact]
    public void IsInBounds_ChecksCorrectly()
    {
        // Arrange
        var grid = new PathfindingGrid(0, 0, 100, 100, cellSize: 1.0);

        // Assert
        grid.IsInBounds(50, 50).ShouldBeTrue();
        grid.IsInBounds(0, 0).ShouldBeTrue();
        grid.IsInBounds(99, 99).ShouldBeTrue();
        grid.IsInBounds(-1, 50).ShouldBeFalse();
        grid.IsInBounds(100, 50).ShouldBeFalse();
    }

    private static Component CreateMockComponent(double x, double y, double width, double height)
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
        component.WidthMicrometers = width;
        component.HeightMicrometers = height;

        return component;
    }
}

public class GridDirectionTests
{
    [Theory]
    [InlineData(0, GridDirection.East)]
    [InlineData(90, GridDirection.North)]
    [InlineData(180, GridDirection.West)]
    [InlineData(270, GridDirection.South)]
    [InlineData(-90, GridDirection.South)]
    [InlineData(360, GridDirection.East)]
    [InlineData(45, GridDirection.NorthEast)]
    [InlineData(135, GridDirection.NorthWest)]
    [InlineData(225, GridDirection.SouthWest)]
    [InlineData(315, GridDirection.SouthEast)]
    [InlineData(20, GridDirection.East)] // Just under 22.5, rounds to East
    [InlineData(25, GridDirection.NorthEast)] // Just over 22.5, rounds to NorthEast
    [InlineData(110, GridDirection.North)] // Just under 112.5, rounds to North
    [InlineData(115, GridDirection.NorthWest)] // Just over 112.5, rounds to NorthWest
    public void FromAngle_ReturnsCorrectDirection(double angle, GridDirection expected)
    {
        var result = GridDirectionExtensions.FromAngle(angle);
        result.ShouldBe(expected);
    }

    [Theory]
    [InlineData(GridDirection.East, 1, 0)]
    [InlineData(GridDirection.North, 0, 1)]
    [InlineData(GridDirection.West, -1, 0)]
    [InlineData(GridDirection.South, 0, -1)]
    [InlineData(GridDirection.NorthEast, 1, 1)]
    [InlineData(GridDirection.NorthWest, -1, 1)]
    [InlineData(GridDirection.SouthWest, -1, -1)]
    [InlineData(GridDirection.SouthEast, 1, -1)]
    public void GetDelta_ReturnsCorrectDelta(GridDirection dir, int expectedDx, int expectedDy)
    {
        var (dx, dy) = dir.GetDelta();
        dx.ShouldBe(expectedDx);
        dy.ShouldBe(expectedDy);
    }

    [Theory]
    [InlineData(GridDirection.East, GridDirection.North, 90)]
    [InlineData(GridDirection.East, GridDirection.South, -90)]
    [InlineData(GridDirection.East, GridDirection.West, 180)]
    [InlineData(GridDirection.North, GridDirection.East, -90)]
    [InlineData(GridDirection.East, GridDirection.NorthEast, 45)]
    [InlineData(GridDirection.East, GridDirection.SouthEast, -45)]
    [InlineData(GridDirection.NorthEast, GridDirection.NorthWest, 90)]
    [InlineData(GridDirection.SouthEast, GridDirection.East, 45)]
    public void GetTurnAngle_CalculatesCorrectly(GridDirection from, GridDirection to, double expected)
    {
        var result = GridDirectionExtensions.GetTurnAngle(from, to);
        result.ShouldBe(expected);
    }

    [Fact]
    public void GetOpposite_ReturnsOpposite()
    {
        GridDirection.East.GetOpposite().ShouldBe(GridDirection.West);
        GridDirection.West.GetOpposite().ShouldBe(GridDirection.East);
        GridDirection.North.GetOpposite().ShouldBe(GridDirection.South);
        GridDirection.South.GetOpposite().ShouldBe(GridDirection.North);
        GridDirection.NorthEast.GetOpposite().ShouldBe(GridDirection.SouthWest);
        GridDirection.NorthWest.GetOpposite().ShouldBe(GridDirection.SouthEast);
        GridDirection.SouthWest.GetOpposite().ShouldBe(GridDirection.NorthEast);
        GridDirection.SouthEast.GetOpposite().ShouldBe(GridDirection.NorthWest);
    }
}

public class RoutingCostCalculatorTests
{
    [Fact]
    public void CalculateMoveCost_StraightMove_ReturnsBaseCost()
    {
        // Arrange
        var calc = new RoutingCostCalculator
        {
            CellSizeMicrometers = 1.0,
            StraightCostPerMicrometer = 1.0,
            TurnCostPer90Degrees = 50.0
        };
        var node = new AStarNode(10, 10, GridDirection.East) { StraightRunLength = 10 };

        // Act - move East (same direction)
        var cost = calc.CalculateMoveCost(node, 11, 10, GridDirection.East);

        // Assert - should be just the distance cost
        cost.ShouldBe(1.0);
    }

    [Fact]
    public void CalculateMoveCost_Turn90_AddsTurnCost()
    {
        // Arrange
        var calc = new RoutingCostCalculator
        {
            CellSizeMicrometers = 1.0,
            StraightCostPerMicrometer = 1.0,
            TurnCostPer90Degrees = 50.0,
            MinStraightRunCells = 5
        };
        var node = new AStarNode(10, 10, GridDirection.East) { StraightRunLength = 10 };

        // Act - turn North (90 degree turn)
        var cost = calc.CalculateMoveCost(node, 10, 11, GridDirection.North);

        // Assert - should be distance + turn penalty
        cost.ShouldBe(51.0); // 1.0 distance + 50.0 turn
    }

    [Fact]
    public void IsTurnValid_SufficientStraightRun_AllowsTurn()
    {
        // Arrange
        var calc = new RoutingCostCalculator { MinStraightRunCells = 5 };
        var node = new AStarNode(10, 10, GridDirection.East) { StraightRunLength = 10 };

        // Act & Assert
        calc.IsTurnValid(node, GridDirection.North).ShouldBeTrue();
    }

    [Fact]
    public void IsTurnValid_InsufficientStraightRun_DeniesTurn()
    {
        // Arrange
        var calc = new RoutingCostCalculator { MinStraightRunCells = 10 };
        var node = new AStarNode(10, 10, GridDirection.East) { StraightRunLength = 5 };

        // Act & Assert
        calc.IsTurnValid(node, GridDirection.North).ShouldBeFalse();
    }

    [Fact]
    public void IsTurnValid_SameDirection_AlwaysValid()
    {
        // Arrange
        var calc = new RoutingCostCalculator { MinStraightRunCells = 100 };
        var node = new AStarNode(10, 10, GridDirection.East) { StraightRunLength = 1 };

        // Act & Assert - continuing same direction is always valid
        calc.IsTurnValid(node, GridDirection.East).ShouldBeTrue();
    }
}

public class WaveguideObstacleTests
{
    [Fact]
    public void AddWaveguideObstacle_BlocksCells()
    {
        // Arrange
        var grid = new PathfindingGrid(0, 0, 100, 100, cellSize: 1.0);
        var connectionId = Guid.NewGuid();

        // Create a simple straight segment from (20,50) to (80,50)
        var segments = new List<PathSegment>
        {
            new StraightSegment(20, 50, 80, 50, 0)
        };

        // Act
        grid.AddWaveguideObstacle(connectionId, segments, waveguideWidth: 2.0);

        // Assert - cells along the waveguide should be blocked
        grid.IsBlocked(50, 50).ShouldBeTrue("Cell on waveguide path should be blocked");
        grid.IsBlocked(10, 10).ShouldBeFalse("Cell away from waveguide should be free");
    }

    [Fact]
    public void RemoveWaveguideObstacle_UnblocksCells()
    {
        // Arrange
        var grid = new PathfindingGrid(0, 0, 100, 100, cellSize: 1.0);
        var connectionId = Guid.NewGuid();

        var segments = new List<PathSegment>
        {
            new StraightSegment(20, 50, 80, 50, 0)
        };

        grid.AddWaveguideObstacle(connectionId, segments, waveguideWidth: 2.0);

        // Act
        grid.RemoveWaveguideObstacle(connectionId);

        // Assert
        grid.IsBlocked(50, 50).ShouldBeFalse("Cell should be unblocked after removal");
    }

    [Fact]
    public void ClearAllWaveguideObstacles_ClearsAll()
    {
        // Arrange
        var grid = new PathfindingGrid(0, 0, 100, 100, cellSize: 1.0);

        // Add two waveguides
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        grid.AddWaveguideObstacle(id1, new[] { new StraightSegment(20, 30, 80, 30, 0) }, 2.0);
        grid.AddWaveguideObstacle(id2, new[] { new StraightSegment(20, 70, 80, 70, 0) }, 2.0);

        // Act
        grid.ClearAllWaveguideObstacles();

        // Assert
        grid.IsBlocked(50, 30).ShouldBeFalse("First waveguide cells should be cleared");
        grid.IsBlocked(50, 70).ShouldBeFalse("Second waveguide cells should be cleared");
    }

    [Fact]
    public void WaveguideObstacle_DoesNotOverwriteComponentObstacle()
    {
        // Arrange
        var grid = new PathfindingGrid(0, 0, 100, 100, cellSize: 1.0, padding: 0);

        // Add component obstacle first
        var component = CreateMockComponent(45, 45, 10, 10);
        grid.AddComponentObstacle(component);

        // Add waveguide that passes through component area
        var connectionId = Guid.NewGuid();
        var segments = new List<PathSegment>
        {
            new StraightSegment(20, 50, 80, 50, 0)
        };
        grid.AddWaveguideObstacle(connectionId, segments, waveguideWidth: 2.0);

        // Act - remove the waveguide
        grid.RemoveWaveguideObstacle(connectionId);

        // Assert - component obstacle should still be there
        grid.IsBlocked(50, 50).ShouldBeTrue("Component obstacle should remain after waveguide removal");
    }

    private static Component CreateMockComponent(double x, double y, double width, double height)
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
        component.WidthMicrometers = width;
        component.HeightMicrometers = height;

        return component;
    }
}

public class PathSmootherSegmentContinuityTests
{
    [Fact]
    public void ConvertToSegments_SegmentsShouldBeContiguous()
    {
        // Arrange - create a grid and a path that requires turns
        var grid = new PathfindingGrid(0, 0, 200, 200, cellSize: 1.0);
        var smoother = new PathSmoother(grid, minBendRadius: 10.0);

        // Create mock pins
        var startComponent = CreateMockComponent(0, 100, 50, 50);
        var endComponent = CreateMockComponent(150, 100, 50, 50);

        var startPin = new PhysicalPin
        {
            Name = "out",
            OffsetXMicrometers = 50,
            OffsetYMicrometers = 25,
            AngleDegrees = 0,
            ParentComponent = startComponent
        };

        var endPin = new PhysicalPin
        {
            Name = "in",
            OffsetXMicrometers = 0,
            OffsetYMicrometers = 25,
            AngleDegrees = 180,
            ParentComponent = endComponent
        };

        // Create a path with two turns (East -> North -> East)
        var gridPath = new List<AStarNode>
        {
            new AStarNode(50, 125, GridDirection.East),
            new AStarNode(70, 125, GridDirection.East),
            new AStarNode(70, 145, GridDirection.North),
            new AStarNode(70, 165, GridDirection.North),
            new AStarNode(90, 165, GridDirection.East),
            new AStarNode(130, 165, GridDirection.East),
            new AStarNode(130, 145, GridDirection.South),
            new AStarNode(130, 125, GridDirection.South),
            new AStarNode(150, 125, GridDirection.East)
        };

        // Act
        var routedPath = smoother.ConvertToSegments(gridPath, startPin, endPin);

        // Assert - segments should be contiguous
        routedPath.Segments.Count.ShouldBeGreaterThan(0, "Should have at least one segment");

        for (int i = 0; i < routedPath.Segments.Count - 1; i++)
        {
            var currentEnd = routedPath.Segments[i].EndPoint;
            var nextStart = routedPath.Segments[i + 1].StartPoint;

            double distance = Math.Sqrt(
                Math.Pow(currentEnd.X - nextStart.X, 2) +
                Math.Pow(currentEnd.Y - nextStart.Y, 2));

            distance.ShouldBeLessThan(2.0,
                $"Gap between segment {i} end ({currentEnd.X:F1}, {currentEnd.Y:F1}) " +
                $"and segment {i + 1} start ({nextStart.X:F1}, {nextStart.Y:F1}) is {distance:F1}µm");
        }
    }

    [Fact]
    public void ConvertToSegments_StartAndEndShouldMatchPins()
    {
        // Arrange
        var grid = new PathfindingGrid(0, 0, 200, 200, cellSize: 1.0);
        var smoother = new PathSmoother(grid, minBendRadius: 10.0);

        var startComponent = CreateMockComponent(0, 100, 50, 50);
        var endComponent = CreateMockComponent(150, 100, 50, 50);

        var startPin = new PhysicalPin
        {
            Name = "out",
            OffsetXMicrometers = 50,
            OffsetYMicrometers = 25,
            AngleDegrees = 0,
            ParentComponent = startComponent
        };

        var endPin = new PhysicalPin
        {
            Name = "in",
            OffsetXMicrometers = 0,
            OffsetYMicrometers = 25,
            AngleDegrees = 180,
            ParentComponent = endComponent
        };

        // Simple straight path
        var gridPath = new List<AStarNode>
        {
            new AStarNode(50, 125, GridDirection.East),
            new AStarNode(100, 125, GridDirection.East),
            new AStarNode(150, 125, GridDirection.East)
        };

        // Act
        var routedPath = smoother.ConvertToSegments(gridPath, startPin, endPin);

        // Assert
        var (expectedStartX, expectedStartY) = startPin.GetAbsolutePosition();
        var (expectedEndX, expectedEndY) = endPin.GetAbsolutePosition();

        var firstSegment = routedPath.Segments.First();
        var lastSegment = routedPath.Segments.Last();

        // Start should match
        double startDist = Math.Sqrt(
            Math.Pow(firstSegment.StartPoint.X - expectedStartX, 2) +
            Math.Pow(firstSegment.StartPoint.Y - expectedStartY, 2));
        startDist.ShouldBeLessThan(1.0, "First segment should start at start pin position");

        // End should match
        double endDist = Math.Sqrt(
            Math.Pow(lastSegment.EndPoint.X - expectedEndX, 2) +
            Math.Pow(lastSegment.EndPoint.Y - expectedEndY, 2));
        endDist.ShouldBeLessThan(1.0, "Last segment should end at end pin position");
    }

    private static Component CreateMockComponent(double x, double y, double width, double height)
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
        component.WidthMicrometers = width;
        component.HeightMicrometers = height;

        return component;
    }
}
