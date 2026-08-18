using CAP_Core.Routing;
using CAP_Core.Routing.MeanderGeneration;
using Shouldly;
using Xunit;

namespace UnitTests.Routing.MeanderGeneration;

/// <summary>
/// Property-style sweep: for a grid of targets between the direct length and 5× the
/// direct length, generation must succeed with the length error inside the tolerance,
/// every bend at or above the radius floor, and the whole path inside the bounds.
/// </summary>
public class MeanderPathGeneratorPropertyTests
{
    private const double DirectLength = 200.0;
    private const double Radius = 5.0;
    private const double Tolerance = 1.0;
    private const double AssertSlack = 1e-6;

    private static readonly MeanderBounds Bounds = new(-5, -60, 205, 60);

    [Theory]
    [InlineData(1.00)] // degenerate: target ≈ direct length
    [InlineData(1.01)] // double S-bend branch (extra below one loop)
    [InlineData(1.04)] // double S-bend near its 90° ceiling
    [InlineData(1.10)]
    [InlineData(1.25)]
    [InlineData(1.50)]
    [InlineData(1.75)]
    [InlineData(2.00)]
    [InlineData(2.50)]
    [InlineData(3.00)]
    [InlineData(3.50)]
    [InlineData(4.00)]
    [InlineData(4.50)]
    [InlineData(5.00)]
    public void Generate_TargetGridBetweenDirectAndFiveTimesDirect_LengthErrorStaysWithinTolerance(
        double factor)
    {
        double target = DirectLength * factor;
        var request = new MeanderRequest(0, 0, 0, DirectLength, 0, 0,
            target, Tolerance, Radius, Bounds);

        var result = new MeanderPathGenerator().Generate(request);

        result.IsSuccess.ShouldBeTrue($"factor {factor}: {result.FailureMessage}");
        var path = result.Path!;
        Math.Abs(path.TotalLengthMicrometers - target).ShouldBeLessThanOrEqualTo(
            Tolerance, $"factor {factor}");
        path.IsValid.ShouldBeTrue($"factor {factor}");

        foreach (var segment in path.Segments)
        {
            if (segment is BendSegment bend)
                bend.RadiusMicrometers.ShouldBeGreaterThanOrEqualTo(Radius, $"factor {factor}");

            var b = PathSegmentBounds.Of(segment);
            b.MinX.ShouldBeGreaterThanOrEqualTo(Bounds.MinX - AssertSlack, $"factor {factor}");
            b.MinY.ShouldBeGreaterThanOrEqualTo(Bounds.MinY - AssertSlack, $"factor {factor}");
            b.MaxX.ShouldBeLessThanOrEqualTo(Bounds.MaxX + AssertSlack, $"factor {factor}");
            b.MaxY.ShouldBeLessThanOrEqualTo(Bounds.MaxY + AssertSlack, $"factor {factor}");
        }

        path.Segments[0].StartPoint.X.ShouldBe(0.0, AssertSlack);
        path.Segments[0].StartPoint.Y.ShouldBe(0.0, AssertSlack);
        path.Segments[^1].EndPoint.X.ShouldBe(DirectLength, AssertSlack);
        path.Segments[^1].EndPoint.Y.ShouldBe(0.0, AssertSlack);
    }
}
