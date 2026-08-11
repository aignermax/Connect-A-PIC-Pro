using Xunit;
using Shouldly;
using CAP_Core.Routing.AStarPathfinder;
using CAP_Core.Routing;
using CAP_Core.Components;
using CAP_Core.Components.Core;

namespace UnitTests.Routing;

/// <summary>
/// Issue #861: the A* smoother must prefer the LARGEST viable bend radius (lower optical
/// loss) and shrink toward the minimum only when space or obstacles force it.
/// </summary>
public class LargestBendRadiusSelectionTests
{
    private static readonly List<double> FoundryRadii = new() { 5, 10, 20, 50 };
    private const double MinRadius = 10.0;

    private static BendBuilder CreateBuilder() => new(MinRadius, FoundryRadii);

    [Fact]
    public void SelectLargestRadiusThatFits_GenerousSpace_PicksLargestAllowed()
    {
        var radius = CreateBuilder().SelectLargestRadiusThatFits(
            sweepDegrees: 90, incomingAvailableMicrometers: 100, outgoingAvailableMicrometers: 100);

        radius.ShouldBe(50);
    }

    [Fact]
    public void SelectLargestRadiusThatFits_LimitedSpace_ShrinksToFit()
    {
        // 90° tangent length equals the radius: only 20 fits into 25µm.
        var radius = CreateBuilder().SelectLargestRadiusThatFits(90, 25, 25);

        radius.ShouldBe(20);
    }

    [Fact]
    public void SelectLargestRadiusThatFits_ConstrainedByTighterSide()
    {
        // Incoming run is generous but the outgoing run only fits 20.
        var radius = CreateBuilder().SelectLargestRadiusThatFits(90, 200, 25);

        radius.ShouldBe(20);
    }

    [Fact]
    public void SelectLargestRadiusThatFits_SpaceBelowMinimum_NeverGoesBelowFloor()
    {
        // Only 5µm would fit into 8µm, but the process floor is 10µm.
        var radius = CreateBuilder().SelectLargestRadiusThatFits(90, 8, 8);

        radius.ShouldBe(MinRadius);
    }

    [Fact]
    public void SelectLargestRadiusThatFits_RejectedRadius_FallsBackToNextSmaller()
    {
        var radius = CreateBuilder().SelectLargestRadiusThatFits(
            90, 100, 100, isRadiusRejected: r => r == 50);

        radius.ShouldBe(20);
    }

    [Fact]
    public void SelectLargestRadiusThatFits_GentlerSweep_AllowsLargerRadius()
    {
        // 45° tangent length is ≈0.414·r, so 50µm fits where a 90° turn would not.
        var radius = CreateBuilder().SelectLargestRadiusThatFits(45, 25, 25);

        radius.ShouldBe(50);
    }

    [Fact]
    public void SelectLargestRadiusThatFits_NoAllowedRadii_ReturnsMinimum()
    {
        var builder = new BendBuilder(12.0, allowedRadii: null);

        builder.SelectLargestRadiusThatFits(90, 100, 100).ShouldBe(12.0);
    }

    [Fact]
    public void PathSmoother_CornerWithGenerousSpace_UsesLargestRadius()
    {
        var grid = new PathfindingGrid(0, 0, 500, 500, cellSize: 1.0, padding: 0);
        var smoother = new PathSmoother(grid, MinRadius, FoundryRadii);
        var (startPin, endPin) = CreateCornerPins();

        var routedPath = smoother.ConvertToSegments(CreateCornerGridPath(), startPin, endPin);

        routedPath.IsInvalidGeometry.ShouldBeFalse();
        var bend = routedPath.Segments.OfType<BendSegment>().ShouldHaveSingleItem();
        bend.RadiusMicrometers.ShouldBe(50);
        AssertContiguous(routedPath);
    }

    [Fact]
    public void PathSmoother_ObstacleInsideCorner_ShrinksRadiusToAvoidIt()
    {
        var grid = new PathfindingGrid(0, 0, 500, 500, cellSize: 1.0, padding: 0);
        // Blocks the inside of the corner where only the 50µm arc sweeps through.
        grid.AddComponentObstacle(CreateMockComponent(230, 260, 10, 10));
        var smoother = new PathSmoother(grid, MinRadius, FoundryRadii);
        var (startPin, endPin) = CreateCornerPins();

        var routedPath = smoother.ConvertToSegments(CreateCornerGridPath(), startPin, endPin);

        routedPath.IsInvalidGeometry.ShouldBeFalse();
        var bend = routedPath.Segments.OfType<BendSegment>().ShouldHaveSingleItem();
        bend.RadiusMicrometers.ShouldBe(20);
        AssertContiguous(routedPath);
    }

    [Fact]
    public void PathSmoother_TightCorner_ShrinksRadiusToFit()
    {
        var grid = new PathfindingGrid(0, 0, 500, 500, cellSize: 1.0, padding: 0);
        var smoother = new PathSmoother(grid, MinRadius, FoundryRadii);
        var startPin = CreatePin(0, 225, pinOffsetX: 50.5, pinOffsetY: 25.5, angle: 0);
        var endPin = CreatePin(55, 280, pinOffsetX: 25.5, pinOffsetY: 0.5, angle: 270);

        // Legs of 30µm: only the 20µm radius fits a 90° corner.
        var gridPath = new List<AStarNode>
        {
            new AStarNode(50, 250, GridDirection.East),
            new AStarNode(65, 250, GridDirection.East),
            new AStarNode(80, 250, GridDirection.North),
            new AStarNode(80, 280, GridDirection.North)
        };

        var routedPath = smoother.ConvertToSegments(gridPath, startPin, endPin);

        routedPath.IsInvalidGeometry.ShouldBeFalse();
        var bend = routedPath.Segments.OfType<BendSegment>().ShouldHaveSingleItem();
        bend.RadiusMicrometers.ShouldBe(20);
        AssertContiguous(routedPath);
    }

    /// <summary>L-shaped path: 200µm East along y=250, then 200µm North along x=250.</summary>
    private static List<AStarNode> CreateCornerGridPath() => new()
    {
        new AStarNode(50, 250, GridDirection.East),
        new AStarNode(150, 250, GridDirection.East),
        new AStarNode(250, 250, GridDirection.North),
        new AStarNode(250, 350, GridDirection.North),
        new AStarNode(250, 450, GridDirection.North)
    };

    /// <summary>
    /// Start pin at (50.5,250.5) facing East; end pin at (250.5,450.5) entered heading North.
    /// Pins sit on grid cell centers so the smoothed path terminates exactly on them.
    /// </summary>
    private static (PhysicalPin Start, PhysicalPin End) CreateCornerPins() =>
        (CreatePin(0, 225, pinOffsetX: 50.5, pinOffsetY: 25.5, angle: 0),
         CreatePin(225, 450, pinOffsetX: 25.5, pinOffsetY: 0.5, angle: 270));

    private static PhysicalPin CreatePin(
        double componentX, double componentY, double pinOffsetX, double pinOffsetY, double angle) =>
        new()
        {
            Name = "pin",
            OffsetXMicrometers = pinOffsetX,
            OffsetYMicrometers = pinOffsetY,
            AngleDegrees = angle,
            ParentComponent = CreateMockComponent(componentX, componentY, 50, 50)
        };

    private static void AssertContiguous(RoutedPath path)
    {
        for (int i = 0; i < path.Segments.Count - 1; i++)
        {
            var gap = Math.Sqrt(
                Math.Pow(path.Segments[i].EndPoint.X - path.Segments[i + 1].StartPoint.X, 2) +
                Math.Pow(path.Segments[i].EndPoint.Y - path.Segments[i + 1].StartPoint.Y, 2));
            gap.ShouldBeLessThan(0.5, $"Gap after segment {i}");
        }
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
