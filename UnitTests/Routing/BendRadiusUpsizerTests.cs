using Xunit;
using Shouldly;
using CAP_Core.Routing.AStarPathfinder;
using CAP_Core.Routing;
using CAP_Core.Components;
using CAP_Core.Components.Core;

namespace UnitTests.Routing;

/// <summary>
/// Issue #861: after all connections are routed, optical bends must grow to the LARGEST
/// allowed radius the free space permits (lower optical loss) — and stay put whenever the
/// straight runs, sibling routes, or components would be violated. The pass must never add
/// or remove segments, so export geometry counts stay stable.
/// </summary>
public class BendRadiusUpsizerTests
{
    private static readonly List<double> FoundryRadii = new() { 5, 10, 20, 50 };

    private static PathfindingGrid CreateGrid() =>
        new(0, 0, 500, 500, cellSize: 1.0, padding: 0);

    [Fact]
    public void TryUpsize_GenerousSpace_GrowsToLargestAllowed()
    {
        var upsizer = new BendRadiusUpsizer(CreateGrid(), FoundryRadii);
        var path = CreateCornerPath();
        var pathStart = path.Segments[0].StartPoint;
        var pathEnd = path.Segments[^1].EndPoint;

        upsizer.TryUpsize(path).ShouldBeTrue();

        path.Segments.Count.ShouldBe(3);
        var bend = path.Segments.OfType<BendSegment>().ShouldHaveSingleItem();
        bend.RadiusMicrometers.ShouldBe(50);
        path.Segments[0].StartPoint.ShouldBe(pathStart);
        path.Segments[^1].EndPoint.ShouldBe(pathEnd);
        AssertContiguous(path);
    }

    [Fact]
    public void TryUpsize_ComponentInsideCorner_ShrinksToNextFittingRadius()
    {
        var grid = CreateGrid();
        // Blocks the inside of the corner where only the 50µm arc sweeps through.
        grid.AddComponentObstacle(CreateMockComponent(230, 260, 10, 10));
        var upsizer = new BendRadiusUpsizer(grid, FoundryRadii);
        var path = CreateCornerPath();

        upsizer.TryUpsize(path).ShouldBeTrue();

        var bend = path.Segments.OfType<BendSegment>().ShouldHaveSingleItem();
        bend.RadiusMicrometers.ShouldBe(20);
        AssertContiguous(path);
    }

    [Fact]
    public void TryUpsize_SiblingRouteInsideCorner_ShrinksToNextFittingRadius()
    {
        var grid = CreateGrid();
        // A sibling waveguide crossing the corner interior where only the 50µm arc sweeps.
        grid.AddWaveguideObstacle(Guid.NewGuid(),
            new[] { new StraightSegment(205, 270, 242, 270, 0) }, waveguideWidth: 1.0);
        var upsizer = new BendRadiusUpsizer(grid, FoundryRadii);
        var path = CreateCornerPath();

        upsizer.TryUpsize(path).ShouldBeTrue();

        var bend = path.Segments.OfType<BendSegment>().ShouldHaveSingleItem();
        bend.RadiusMicrometers.ShouldBe(20);
        AssertContiguous(path);
    }

    [Fact]
    public void TryUpsize_TightStraightRuns_KeepsExistingRadius()
    {
        var upsizer = new BendRadiusUpsizer(CreateGrid(), FoundryRadii);
        // 8µm legs: even the 20µm radius needs 10µm of extra tangent length.
        var path = CreateCornerPath(incomingLength: 8, outgoingLength: 8);

        upsizer.TryUpsize(path).ShouldBeFalse();

        var bend = path.Segments.OfType<BendSegment>().ShouldHaveSingleItem();
        bend.RadiusMicrometers.ShouldBe(10);
        AssertContiguous(path);
    }

    [Fact]
    public void TryUpsize_OneTightSide_ConstrainedByTighterRun()
    {
        var upsizer = new BendRadiusUpsizer(CreateGrid(), FoundryRadii);
        // Outgoing run only leaves room for the 20µm radius (needs 10µm extra tangent).
        var path = CreateCornerPath(incomingLength: 190, outgoingLength: 12);

        upsizer.TryUpsize(path).ShouldBeTrue();

        var bend = path.Segments.OfType<BendSegment>().ShouldHaveSingleItem();
        bend.RadiusMicrometers.ShouldBe(20);
        AssertContiguous(path);
    }

    [Fact]
    public void TryUpsize_BendAlreadyAtLargestAllowed_ReturnsFalse()
    {
        var upsizer = new BendRadiusUpsizer(CreateGrid(), FoundryRadii);
        var path = CreateCornerPath(radius: 50);

        upsizer.TryUpsize(path).ShouldBeFalse();
    }

    [Fact]
    public void TryUpsize_BendWithoutStraightNeighbours_IsLeftUntouched()
    {
        var upsizer = new BendRadiusUpsizer(CreateGrid(), FoundryRadii);
        var path = new RoutedPath();
        path.Segments.Add(new BendSegment(240, 260, 10, 0, 90));

        upsizer.TryUpsize(path).ShouldBeFalse();

        path.Segments.OfType<BendSegment>().ShouldHaveSingleItem()
            .RadiusMicrometers.ShouldBe(10);
    }

    /// <summary>
    /// L-shaped route with its corner apex at (250,250): a straight running East along
    /// y=250, a 90° East→North bend of <paramref name="radius"/>, then a straight running
    /// North along x=250. Leg lengths are measured excluding the bend's tangent setback.
    /// </summary>
    private static RoutedPath CreateCornerPath(
        double incomingLength = 190, double outgoingLength = 190, double radius = 10)
    {
        double bendStartX = 250 - radius;
        double bendEndY = 250 + radius;
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(
            bendStartX - incomingLength, 250, bendStartX, 250, 0));
        path.Segments.Add(new BendSegment(bendStartX, bendEndY, radius, 0, 90));
        path.Segments.Add(new StraightSegment(
            250, bendEndY, 250, bendEndY + outgoingLength, 90));
        return path;
    }

    private static void AssertContiguous(RoutedPath path)
    {
        for (int i = 0; i < path.Segments.Count - 1; i++)
        {
            var gap = Math.Sqrt(
                Math.Pow(path.Segments[i].EndPoint.X - path.Segments[i + 1].StartPoint.X, 2) +
                Math.Pow(path.Segments[i].EndPoint.Y - path.Segments[i + 1].StartPoint.Y, 2));
            gap.ShouldBeLessThan(0.01, $"Gap after segment {i}");
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
