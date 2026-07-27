using CAP_Core.Routing.AStarPathfinder;
using Shouldly;
using Xunit;

namespace UnitTests.Routing;

/// <summary>
/// Regression tests ensuring the A* pathfinder never returns a self-intersecting
/// path (e.g. a full 360° loop at the start pin) — a waveguide crossing itself
/// has no valid optical model.
/// </summary>
public class PathLoopDetectorTests
{
    [Fact]
    public void StraightPath_IsNotSelfIntersecting()
    {
        var path = MakePath((0, 0), (1, 0), (2, 0), (3, 0));
        PathLoopDetector.IsSelfIntersecting(path).ShouldBeFalse();
    }

    [Fact]
    public void UTurnPath_IsNotSelfIntersecting()
    {
        // East, then North, then West — a legal U-shape without overlap.
        var path = MakePath((0, 0), (1, 0), (2, 0), (2, 1), (2, 2), (1, 2), (0, 2));
        PathLoopDetector.IsSelfIntersecting(path).ShouldBeFalse();
    }

    [Fact]
    public void FullLoopRevisitingCell_IsSelfIntersecting()
    {
        // A 360° box that returns onto its own first cell (the self-loop symptom).
        var path = MakePath(
            (0, 0), (1, 0), (2, 0),
            (2, 1), (2, 2),
            (1, 2), (0, 2),
            (0, 1), (0, 0),
            (1, 0));
        PathLoopDetector.IsSelfIntersecting(path).ShouldBeTrue();
    }

    [Fact]
    public void CrossingDiagonalSteps_AreSelfIntersecting()
    {
        // Two diagonal steps of opposite slope through the same unit square
        // cross between the cells without sharing any cell.
        var path = MakePath((0, 0), (1, 1), (1, 0), (0, 1));
        PathLoopDetector.IsSelfIntersecting(path).ShouldBeTrue();
    }

    [Fact]
    public void ParallelDiagonalSteps_AreNotSelfIntersecting()
    {
        var path = MakePath((0, 0), (1, 1), (2, 2), (3, 3));
        PathLoopDetector.IsSelfIntersecting(path).ShouldBeFalse();
    }

    [Fact]
    public void FindPath_PinPointingAwayFromGoal_ReturnsLoopFreePath()
    {
        // Start pin points East while the goal lies West: the search must make
        // a U-turn, never a 360° self-crossing circle.
        var grid = new PathfindingGrid(0, 0, 200, 200, cellSize: 1.0);
        var costCalculator = new RoutingCostCalculator
        {
            CellSizeMicrometers = 1.0,
            MinStraightRunCells = 5,
            MinPinEscapeCells = 5,
        };
        var pathfinder = new AStarPathfinder(grid, costCalculator);

        var path = pathfinder.FindPath(
            100, 100, GridDirection.East,
            20, 100, GridDirection.West);

        path.ShouldNotBeNull();
        PathLoopDetector.IsSelfIntersecting(path).ShouldBeFalse(
            "the returned path must never cross itself");
    }

    private static List<AStarNode> MakePath(params (int X, int Y)[] cells) =>
        cells.Select(c => new AStarNode(c.X, c.Y, GridDirection.East)).ToList();
}
