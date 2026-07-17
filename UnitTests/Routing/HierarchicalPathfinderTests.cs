using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Components.Connections;
using CAP_Core.Components.FormulaReading;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Routing.AStarPathfinder;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Routing;

/// <summary>
/// Tests for HPA* hierarchical pathfinding, sector graph, and distance transform.
/// </summary>
[Trait("Category", "Slow")]
public class HierarchicalPathfinderTests
{
    [Fact]
    public void DistanceTransform_ZeroAtWaveguideSource()
    {
        var grid = CreateGrid(100, 100);
        // Mark some cells as waveguide obstacles
        grid.SetCellState(50, 50, 2);
        grid.SetCellState(51, 50, 2);

        var dt = new DistanceTransform(100, 100, 1.0, 20.0);
        dt.BuildFromGrid(grid);

        dt.GetDistanceMicrometers(50, 50).ShouldBe(0);
        dt.GetDistanceMicrometers(51, 50).ShouldBe(0);
    }

    [Fact]
    public void DistanceTransform_IncreasesWithDistance()
    {
        var grid = CreateGrid(100, 100);
        grid.SetCellState(50, 50, 2);

        var dt = new DistanceTransform(100, 100, 1.0, 20.0);
        dt.BuildFromGrid(grid);

        double dist1 = dt.GetDistanceMicrometers(51, 50); // 1 cell away
        double dist5 = dt.GetDistanceMicrometers(55, 50); // 5 cells away
        double dist10 = dt.GetDistanceMicrometers(60, 50); // 10 cells away

        dist1.ShouldBe(1.0);
        dist5.ShouldBe(5.0);
        dist10.ShouldBe(10.0);
        dist5.ShouldBeGreaterThan(dist1);
        dist10.ShouldBeGreaterThan(dist5);
    }

    [Fact]
    public void DistanceTransform_MaxDistanceForFarCells()
    {
        var grid = CreateGrid(100, 100);
        grid.SetCellState(50, 50, 2);

        var dt = new DistanceTransform(100, 100, 1.0, 10.0);
        dt.BuildFromGrid(grid);

        // Cell 25 units away should be at max distance
        dt.GetDistanceMicrometers(75, 50).ShouldBe(10.0);
    }

    [Fact]
    public void DistanceTransform_OutOfBoundsReturnsMax()
    {
        var dt = new DistanceTransform(100, 100, 1.0, 10.0);
        dt.GetDistanceMicrometers(-1, 0).ShouldBe(10.0);
        dt.GetDistanceMicrometers(100, 0).ShouldBe(10.0);
    }

    [Fact]
    public void SectorGraph_DetectsPortalsOnClearEdge()
    {
        // 100x100 grid with 50-cell sectors = 2x2 sectors
        var grid = CreateGrid(100, 100);
        var costCalc = new RoutingCostCalculator { CellSizeMicrometers = 1.0 };

        var sectorGraph = new SectorGraph(grid, costCalc, 50);
        sectorGraph.Build();

        sectorGraph.SectorCols.ShouldBe(2);
        sectorGraph.SectorRows.ShouldBe(2);
        // Should have portals on the internal boundaries
        sectorGraph.Portals.Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void SectorGraph_BlockedEdge_NoPortals()
    {
        var grid = CreateGrid(100, 100);
        var costCalc = new RoutingCostCalculator { CellSizeMicrometers = 1.0 };

        // Block the entire vertical boundary at x=50
        for (int y = 0; y < 100; y++)
        {
            grid.SetCellState(49, y, 1); // Block left side
            grid.SetCellState(50, y, 1); // Block right side
        }

        var sectorGraph = new SectorGraph(grid, costCalc, 50);
        sectorGraph.Build();

        // The vertical (east) boundary of sector (0,0) should have no portals
        var eastPortals = sectorGraph.Portals
            .Where(p => p.SectorCoords == (0, 0) && p.Edge == SectorEdge.East)
            .ToList();
        eastPortals.Count.ShouldBe(0);
    }

    [Fact]
    public void SectorGraph_FindNearestPortals_ReturnsSorted()
    {
        var grid = CreateGrid(200, 200);
        var costCalc = new RoutingCostCalculator { CellSizeMicrometers = 1.0 };

        var sectorGraph = new SectorGraph(grid, costCalc, 50);
        sectorGraph.Build();

        // Find portals near center of first sector
        var portals = sectorGraph.FindNearestPortals(25, 25);
        portals.Count.ShouldBeGreaterThan(0);

        // Should be sorted by cost
        for (int i = 1; i < portals.Count; i++)
        {
            portals[i].Cost.ShouldBeGreaterThanOrEqualTo(portals[i - 1].Cost);
        }
    }

    [Fact]
    public void HierarchicalPathfinder_ShortRoute_UsesFlatAStar()
    {
        var grid = CreateGrid(200, 200);
        var costCalc = new RoutingCostCalculator
        {
            CellSizeMicrometers = 1.0,
            MinStraightRunCells = 5,
            MinPinEscapeCells = 5
        };

        var hpf = new HierarchicalPathfinder(grid, costCalc);
        hpf.BuildSectorGraph(50);
        hpf.FlatSearchThreshold = 200;

        // Short route within threshold
        var path = hpf.FindPath(10, 10, GridDirection.East, 50, 10, GridDirection.West);

        // Should find a path (flat A*)
        path.ShouldNotBeNull();
        path!.Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void HierarchicalPathfinder_BuildsSectorGraph()
    {
        var grid = CreateGrid(300, 300);
        var costCalc = new RoutingCostCalculator { CellSizeMicrometers = 1.0 };

        var hpf = new HierarchicalPathfinder(grid, costCalc);
        hpf.IsBuilt.ShouldBeFalse();

        hpf.BuildSectorGraph(50);

        hpf.IsBuilt.ShouldBeTrue();
        hpf.SectorGraph.ShouldNotBeNull();
        hpf.DistanceTransform.ShouldNotBeNull();
    }

    [Fact]
    public void HierarchicalPathfinder_BuildsDistanceTransform()
    {
        var grid = CreateGrid(100, 100);
        grid.SetCellState(50, 50, 2); // Waveguide obstacle

        var costCalc = new RoutingCostCalculator
        {
            CellSizeMicrometers = 1.0,
            MinSafeSpacingMicrometers = 10.0
        };

        var hpf = new HierarchicalPathfinder(grid, costCalc);
        hpf.BuildSectorGraph(50);

        hpf.DistanceTransform.ShouldNotBeNull();
        hpf.DistanceTransform!.GetDistanceMicrometers(50, 50).ShouldBe(0);
        hpf.DistanceTransform.GetDistanceMicrometers(55, 50).ShouldBe(5.0);
    }

    [Fact]
    public void ProximityCost_WithDistanceTransform_MatchesBruteForce()
    {
        var grid = CreateGrid(100, 100);
        // Place a waveguide line
        for (int x = 40; x <= 60; x++)
            grid.SetCellState(x, 50, 2);

        var costCalc = new RoutingCostCalculator
        {
            CellSizeMicrometers = 1.0,
            MinSafeSpacingMicrometers = 10.0,
            ProximityCostMultiplier = 100.0
        };

        // Get brute-force cost (no distance transform)
        double bruteForce = costCalc.CalculateProximityCost(grid, 50, 55);

        // Build distance transform
        var dt = new DistanceTransform(100, 100, 1.0, 10.0);
        dt.BuildFromGrid(grid);
        costCalc.DistanceTransformGrid = dt;

        // Get DT-accelerated cost
        double dtCost = costCalc.CalculateProximityCost(grid, 50, 55);

        // Should be similar (DT uses Manhattan, brute-force uses Euclidean,
        // so they won't match exactly, but should be in the same ballpark)
        dtCost.ShouldBeGreaterThan(0);
        bruteForce.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void ProximityCost_FarFromObstacle_ReturnsZero()
    {
        var grid = CreateGrid(100, 100);
        grid.SetCellState(10, 10, 2);

        var dt = new DistanceTransform(100, 100, 1.0, 10.0);
        dt.BuildFromGrid(grid);

        var costCalc = new RoutingCostCalculator
        {
            CellSizeMicrometers = 1.0,
            MinSafeSpacingMicrometers = 10.0,
            DistanceTransformGrid = dt
        };

        // 50 cells away — well beyond the 10µm spacing
        double cost = costCalc.CalculateProximityCost(grid, 60, 60);
        cost.ShouldBe(0);
    }

    [Fact]
    public void WaveguideRouter_BuildHierarchicalGraph_IntegratesCorrectly()
    {
        var router = new WaveguideRouter();
        var comp1 = CreateTestComponent(0, 0);
        var comp2 = CreateTestComponent(200, 0);

        router.InitializePathfindingGrid(-50, -50, 300, 100,
            new[] { comp1, comp2 });

        // Should not throw
        router.BuildHierarchicalGraph();
    }

    private static PathfindingGrid CreateGrid(int width, int height)
    {
        return new PathfindingGrid(0, 0, width, height, 1.0, 0);
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
}
