using CAP_Core.Routing;
using CAP_Core.Routing.AStarPathfinder;
using Shouldly;
using Xunit;

namespace UnitTests.Routing;

/// <summary>
/// Waveguide obstacles must rasterize <see cref="BendSegment"/> arcs by ARC LENGTH:
/// angle-based sampling leaves gaps larger than a grid cell on large-radius arcs, letting
/// sibling routes thread straight through a registered bend.
/// </summary>
public class ArcObstacleRasterizationTests
{
    private const double WaveguideWidth = 4.0;

    [Theory]
    [InlineData(50.0)]
    [InlineData(140.0)]
    [InlineData(300.0)]
    public void AddWaveguideObstacle_LargeRadiusArc_BlocksTheWholeArcWithoutGaps(double radius)
    {
        var grid = new PathfindingGrid(0, 0, 1000, 1000, cellSize: 1.0);
        var bend = new BendSegment(500, 500, radius, startAngle: 0, sweepAngle: 90);

        grid.AddWaveguideObstacle(Guid.NewGuid(), new PathSegment[] { bend }, WaveguideWidth);

        foreach (var (x, y) in ArcSampling.SamplePoints(bend, maxStepMicrometers: 0.5))
        {
            var (gx, gy) = grid.PhysicalToGrid(x, y);
            grid.IsBlocked(gx, gy).ShouldBeTrue(
                $"arc point ({x:F1}, {y:F1}) of the r={radius} bend must be a solid obstacle");
        }
    }

    [Fact]
    public void ArcSampling_StepNeverExceedsRequestedArcLength()
    {
        var bend = new BendSegment(0, 0, 200, startAngle: 0, sweepAngle: 90);

        var points = ArcSampling.SamplePoints(bend, maxStepMicrometers: 1.0).ToList();

        points.Count.ShouldBeGreaterThan(300, "a 90° arc of r=200 is ~314 µm long");
        for (int i = 1; i < points.Count; i++)
        {
            double dx = points[i].X - points[i - 1].X;
            double dy = points[i].Y - points[i - 1].Y;
            Math.Sqrt(dx * dx + dy * dy).ShouldBeLessThanOrEqualTo(1.0 + 1e-6);
        }
    }

    [Fact]
    public void ArcSampling_IncludesBothArcEndpoints()
    {
        var bend = new BendSegment(100, 100, 40, startAngle: 45, sweepAngle: -120);

        var points = ArcSampling.SamplePoints(bend, maxStepMicrometers: 2.0).ToList();

        points[0].X.ShouldBe(bend.StartPoint.X, 1e-9);
        points[0].Y.ShouldBe(bend.StartPoint.Y, 1e-9);
        points[^1].X.ShouldBe(bend.EndPoint.X, 1e-9);
        points[^1].Y.ShouldBe(bend.EndPoint.Y, 1e-9);
    }
}
