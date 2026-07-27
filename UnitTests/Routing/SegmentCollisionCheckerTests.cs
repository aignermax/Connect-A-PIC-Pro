using CAP_Core.Routing;
using CAP_Core.Routing.AStarPathfinder;
using Shouldly;
using Xunit;

namespace UnitTests.Routing;

/// <summary>
/// Tests for <see cref="SegmentCollisionChecker"/>, which validates the path
/// smoother's geometrically constructed terminal approach against the grid
/// (issue #704, bug 3: unchecked approach bends cut through foreign waveguides).
/// </summary>
public class SegmentCollisionCheckerTests
{
    /// <summary>Waveguide obstacle width generous enough to firmly mark grid rows.</summary>
    private const double ObstacleWidthMicrometers = 6.0;

    /// <summary>Creates a grid with a horizontal waveguide obstacle from (50,100) to (150,100).</summary>
    private static PathfindingGrid CreateGridWithHorizontalWaveguide()
    {
        var grid = new PathfindingGrid(0, 0, 200, 200, cellSize: 4.0);
        var obstacle = new RoutedPath();
        obstacle.Segments.Add(new StraightSegment(50, 100, 150, 100, 0));
        grid.AddWaveguideObstacle(Guid.NewGuid(), obstacle.Segments, ObstacleWidthMicrometers);
        return grid;
    }

    [Fact]
    public void StraightCrossingForeignWaveguide_IsBlocked()
    {
        var grid = CreateGridWithHorizontalWaveguide();
        var checker = new SegmentCollisionChecker(grid);

        var crossing = new StraightSegment(100, 50, 100, 150, 90);

        checker.IsAnyBlocked(new[] { crossing }).ShouldBeTrue(
            "a straight cutting through a foreign waveguide must be detected");
    }

    [Fact]
    public void StraightParallelToWaveguide_IsFree()
    {
        var grid = CreateGridWithHorizontalWaveguide();
        var checker = new SegmentCollisionChecker(grid);

        var parallel = new StraightSegment(50, 130, 150, 130, 0);

        checker.IsAnyBlocked(new[] { parallel }).ShouldBeFalse();
    }

    [Fact]
    public void StraightStoppingBeforeWaveguide_IsFree()
    {
        var grid = CreateGridWithHorizontalWaveguide();
        var checker = new SegmentCollisionChecker(grid);

        var approach = new StraightSegment(100, 50, 100, 88, 90);

        checker.IsAnyBlocked(new[] { approach }).ShouldBeFalse();
    }

    [Fact]
    public void BendSweepingAcrossWaveguide_IsBlocked()
    {
        var grid = CreateGridWithHorizontalWaveguide();
        var checker = new SegmentCollisionChecker(grid);

        // Quarter bend from heading 270° (up) to 180° (west) whose arc sweeps
        // across the y=100 waveguide line — the exact geometry of the bug-3
        // repro's terminal approach.
        var bend = new BendSegment(centerX: 105, centerY: 105, radius: 10,
            startAngle: 270, sweepAngle: -90);

        checker.IsAnyBlocked(new[] { bend }).ShouldBeTrue(
            "an approach bend sweeping through a foreign waveguide must be detected");
    }

    [Fact]
    public void BendInFreeSpace_IsFree()
    {
        var grid = CreateGridWithHorizontalWaveguide();
        var checker = new SegmentCollisionChecker(grid);

        var bend = new BendSegment(centerX: 100, centerY: 160, radius: 10,
            startAngle: 270, sweepAngle: -90);

        checker.IsAnyBlocked(new[] { bend }).ShouldBeFalse();
    }

    [Fact]
    public void ExcludedCells_AreNotTreatedAsCollisions()
    {
        // A terminal approach necessarily enters the component of the pin it lands on
        // (pins can sit deep inside the body), so those cells must not count as
        // collisions — otherwise every route into a pin gets flagged and A* is
        // needlessly discarded for the blind Manhattan fallback (#704 review).
        var grid = new PathfindingGrid(0, 0, 200, 200, cellSize: 4.0);

        // Block the exact cells the vertical crossing will sample, and remember them.
        var crossing = new StraightSegment(100, 50, 100, 150, 90);
        var ownCells = new HashSet<(int x, int y)>();
        for (double y = 50; y <= 150; y += 2.0)
        {
            var cell = grid.PhysicalToGrid(100, y);
            grid.SetCellState(cell.gridX, cell.gridY, 1);
            ownCells.Add((cell.gridX, cell.gridY));
        }

        // Without exclusion the crossing is blocked; excluding those cells clears it.
        new SegmentCollisionChecker(grid).IsAnyBlocked(new[] { crossing })
            .ShouldBeTrue("the crossing runs through blocked cells");
        new SegmentCollisionChecker(grid, ownCells).IsAnyBlocked(new[] { crossing })
            .ShouldBeFalse("cells belonging to the route's own endpoint are excluded");
    }

    [Fact]
    public void Exclusion_StillDetectsForeignObstacles()
    {
        // Excluding the endpoint's own cells must NOT blind the checker to a real
        // foreign waveguide crossing near the pin (#704 bug 3 must still be caught).
        var grid = CreateGridWithHorizontalWaveguide();
        var crossing = new StraightSegment(100, 50, 100, 150, 90);

        // Exclude a cell far from the foreign waveguide — detection must survive.
        var unrelated = new HashSet<(int x, int y)> { grid.PhysicalToGrid(10, 10) };
        new SegmentCollisionChecker(grid, unrelated).IsAnyBlocked(new[] { crossing })
            .ShouldBeTrue("a foreign waveguide crossing is still detected");
    }
}
