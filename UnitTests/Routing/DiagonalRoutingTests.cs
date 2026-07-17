using Xunit;
using Shouldly;
using CAP_Core.Routing.AStarPathfinder;
using CAP_Core.Components;
using CAP_Core.Components.Core;

namespace UnitTests.Routing;

/// <summary>
/// Tests for 8-direction (45° diagonal) octile routing (Issue #552).
/// </summary>
[Trait("Category", "Slow")]
public class DiagonalRoutingTests
{
    private const double CellSize = 1.0;

    private static (PathfindingGrid grid, RoutingCostCalculator calc) CreateSetup(
        int minStraightRun = 4)
    {
        var grid = new PathfindingGrid(0, 0, 100, 100, cellSize: CellSize);
        var calc = new RoutingCostCalculator
        {
            CellSizeMicrometers = CellSize,
            MinStraightRunCells = minStraightRun
        };
        return (grid, calc);
    }

    private static double PhysicalPathLength(List<AStarNode> path)
    {
        double length = 0;
        for (int i = 1; i < path.Count; i++)
        {
            int dx = path[i].X - path[i - 1].X;
            int dy = path[i].Y - path[i - 1].Y;
            length += Math.Sqrt(dx * dx + dy * dy) * CellSize;
        }
        return length;
    }

    [Fact]
    public void FindPath_DiagonallyOffsetPins_UsesDiagonalAndIsShorterThanManhattan()
    {
        var (grid, calc) = CreateSetup();
        var pathfinder = new AStarPathfinder(grid, calc);

        var path = pathfinder.FindPath(10, 10, GridDirection.East, 80, 50, GridDirection.East);

        path.ShouldNotBeNull();
        path.ShouldContain(n => n.Direction.IsDiagonal(),
            "Path between diagonally offset pins should use 45° segments");

        double manhattanDistance = (Math.Abs(80 - 10) + Math.Abs(50 - 10)) * CellSize;
        PhysicalPathLength(path).ShouldBeLessThan(manhattanDistance,
            "Octile path should be shorter than the pure Manhattan path");
    }

    [Fact]
    public void FindPath_DiagonalStep_NeverCutsBlockedCorners()
    {
        var (grid, calc) = CreateSetup();

        // Obstacle in the middle forces the path to route around its corners
        var obstacle = CreateMockComponent(40, 30, 20, 40);
        grid.AddComponentObstacle(obstacle);

        var pathfinder = new AStarPathfinder(grid, calc);
        var path = pathfinder.FindPath(10, 50, GridDirection.East, 90, 50, GridDirection.East);

        path.ShouldNotBeNull();

        for (int i = 1; i < path.Count; i++)
        {
            int dx = path[i].X - path[i - 1].X;
            int dy = path[i].Y - path[i - 1].Y;
            if (dx == 0 || dy == 0)
                continue; // Not a diagonal step

            grid.IsBlocked(path[i - 1].X + dx, path[i - 1].Y).ShouldBeFalse(
                $"Diagonal step at ({path[i - 1].X},{path[i - 1].Y}) cuts a blocked X-neighbor");
            grid.IsBlocked(path[i - 1].X, path[i - 1].Y + dy).ShouldBeFalse(
                $"Diagonal step at ({path[i - 1].X},{path[i - 1].Y}) cuts a blocked Y-neighbor");
        }
    }

    [Theory]
    [InlineData(10, 10, GridDirection.East, 80, 50, GridDirection.East)]
    [InlineData(10, 50, GridDirection.East, 90, 50, GridDirection.East)]
    [InlineData(10, 10, GridDirection.East, 50, 80, GridDirection.North)]
    [InlineData(20, 80, GridDirection.South, 80, 20, GridDirection.East)]
    public void OctileHeuristic_IsAdmissible_NeverExceedsActualPathCost(
        int startX, int startY, GridDirection startDir,
        int endX, int endY, GridDirection endDir)
    {
        var (grid, calc) = CreateSetup();
        var pathfinder = new AStarPathfinder(grid, calc) { GoalTolerance = 0 };

        var path = pathfinder.FindPath(startX, startY, startDir, endX, endY, endDir);
        path.ShouldNotBeNull();

        // Re-sum the actual move costs along the found path (empty grid: no
        // proximity or pin-zone costs contribute)
        double actualCost = 0;
        for (int i = 1; i < path.Count; i++)
        {
            actualCost += calc.CalculateMoveCost(
                path[i - 1], path[i].X, path[i].Y, path[i].Direction);
        }

        double heuristic = calc.CalculateHeuristic(
            startX, startY, startDir, endX, endY, endDir);

        heuristic.ShouldBeLessThanOrEqualTo(actualCost + 1e-6,
            "Octile heuristic must never overestimate the real path cost (admissibility)");
    }

    [Fact]
    public void PathSmoother_DiagonalSegment_Produces45DegreeBendsWithValidRadius()
    {
        const double minBendRadius = 10.0;
        var grid = new PathfindingGrid(0, 0, 200, 200, cellSize: 1.0);
        var smoother = new PathSmoother(grid, minBendRadius);

        var startComponent = CreateMockComponent(0, 100, 50, 50);
        var endComponent = CreateMockComponent(150.5, 120.5, 50, 50);

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

        // East → NorthEast (45°) → East path with generous straight runs
        var gridPath = new List<AStarNode>
        {
            new AStarNode(50, 125, GridDirection.East),
            new AStarNode(80, 125, GridDirection.East),
            new AStarNode(81, 126, GridDirection.NorthEast),
            new AStarNode(100, 145, GridDirection.NorthEast),
            new AStarNode(101, 145, GridDirection.East),
            new AStarNode(150, 145, GridDirection.East)
        };

        var routedPath = smoother.ConvertToSegments(gridPath, startPin, endPin);

        routedPath.IsInvalidGeometry.ShouldBeFalse(
            "45° corners must not be rejected by the PathSmoother");
        routedPath.Segments.Count.ShouldBeGreaterThan(0);

        var bends = routedPath.Segments.OfType<CAP_Core.Routing.BendSegment>().ToList();
        bends.ShouldNotBeEmpty("Diagonal path should produce bend segments");

        foreach (var bend in bends)
        {
            bend.RadiusMicrometers.ShouldBeGreaterThanOrEqualTo(minBendRadius,
                "Every bend must respect the minimum bend radius");
        }

        bends.ShouldContain(b => Math.Abs(Math.Abs(b.SweepAngleDegrees) - 45) < 1.0,
            "Path with a diagonal segment should contain a 45° bend");

        // Segments must remain contiguous (no jumps at 45° corners)
        for (int i = 0; i < routedPath.Segments.Count - 1; i++)
        {
            var currentEnd = routedPath.Segments[i].EndPoint;
            var nextStart = routedPath.Segments[i + 1].StartPoint;
            double gap = Math.Sqrt(
                Math.Pow(currentEnd.X - nextStart.X, 2) +
                Math.Pow(currentEnd.Y - nextStart.Y, 2));
            gap.ShouldBeLessThan(2.0,
                $"Gap of {gap:F2}µm between segments {i} and {i + 1}");
        }
    }

    [Fact]
    public void FindPath_CardinalPins_StillWorks()
    {
        var (grid, calc) = CreateSetup();
        var pathfinder = new AStarPathfinder(grid, calc);

        var path = pathfinder.FindPath(10, 50, GridDirection.East, 90, 50, GridDirection.East);

        path.ShouldNotBeNull();
        // A straight cardinal connection stays straight — no diagonal detours
        path.ShouldAllBe(n => n.Direction == GridDirection.East);
        path.ShouldAllBe(n => n.Y == 50);
    }

    [Fact]
    public void CalculateMoveCost_DiagonalStep_CostsSqrt2()
    {
        var calc = new RoutingCostCalculator
        {
            CellSizeMicrometers = 1.0,
            StraightCostPerMicrometer = 1.0,
            TurnCostPer90Degrees = 50.0
        };
        var node = new AStarNode(10, 10, GridDirection.NorthEast) { StraightRunLength = 10 };

        var cost = calc.CalculateMoveCost(node, 11, 11, GridDirection.NorthEast);

        cost.ShouldBe(Math.Sqrt(2.0), tolerance: 1e-9);
    }

    [Fact]
    public void CalculateMoveCost_45DegreeTurn_CostsHalfOf90DegreeTurn()
    {
        var calc = new RoutingCostCalculator
        {
            CellSizeMicrometers = 1.0,
            StraightCostPerMicrometer = 1.0,
            TurnCostPer90Degrees = 50.0
        };
        var node = new AStarNode(10, 10, GridDirection.East) { StraightRunLength = 10 };

        var cost = calc.CalculateMoveCost(node, 11, 11, GridDirection.NorthEast);

        // √2 distance + half of the 90° turn cost
        cost.ShouldBe(Math.Sqrt(2.0) + 25.0, tolerance: 1e-9);
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
