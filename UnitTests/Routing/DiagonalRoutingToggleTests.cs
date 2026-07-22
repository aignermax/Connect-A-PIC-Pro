using Xunit;
using Shouldly;
using CAP_Core.Routing;
using CAP_Core.Routing.AStarPathfinder;

namespace UnitTests.Routing;

/// <summary>
/// Tests for the diagonal-routing opt-in toggle: with diagonals disabled the
/// search must behave like classic 4-direction routing (Issue #552 follow-up).
/// </summary>
public class DiagonalRoutingToggleTests
{
    private const double CellSize = 1.0;

    private static (PathfindingGrid grid, RoutingCostCalculator calc) CreateSetup()
    {
        var grid = new PathfindingGrid(0, 0, 100, 100, cellSize: CellSize);
        var calc = new RoutingCostCalculator
        {
            CellSizeMicrometers = CellSize,
            MinStraightRunCells = 4
        };
        return (grid, calc);
    }

    [Fact]
    public void FindPath_DiagonalsDisabled_UsesOnlyCardinalDirections()
    {
        var (grid, calc) = CreateSetup();
        calc.UseDiagonals = false;
        var pathfinder = new AStarPathfinder(grid, calc) { UseDiagonals = false };

        var path = pathfinder.FindPath(10, 10, GridDirection.East, 80, 50, GridDirection.East);

        path.ShouldNotBeNull();
        path.ShouldAllBe(n => !n.Direction.IsDiagonal());
    }

    [Fact]
    public void FindPath_DiagonalsDisabled_StillReachesTheGoal()
    {
        var (grid, calc) = CreateSetup();
        calc.UseDiagonals = false;
        var pathfinder = new AStarPathfinder(grid, calc) { UseDiagonals = false };

        var path = pathfinder.FindPath(10, 10, GridDirection.East, 80, 50, GridDirection.East);

        path.ShouldNotBeNull();
        var last = path[^1];
        last.X.ShouldBeInRange(77, 80);
        last.Y.ShouldBe(50);
    }

    [Fact]
    public void Router_DiagonalRoutingProperty_DefaultsToEnabledAtLibraryLevel()
    {
        // The application layer opts out by default; the library keeps
        // diagonals on so existing octile behavior stays the default for
        // direct WaveguideRouter consumers (tests, LayoutTestRunner).
        new WaveguideRouter().UseDiagonalRouting.ShouldBeTrue();
    }
}
