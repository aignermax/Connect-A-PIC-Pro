using CAP_Core.Routing;
using CAP_Core.Routing.MeanderGeneration;
using Shouldly;
using Xunit;

namespace UnitTests.Routing.MeanderGeneration;

public class MeanderPathGeneratorTests
{
    private const double AssertSlack = 1e-6;

    private static MeanderRequest AlignedRequest(
        double target, MeanderBounds? bounds = null, double tolerance = 0.5, double radius = 5.0)
        => new(0, 0, 0, 100, 0, 0, target, tolerance, radius,
            bounds ?? new MeanderBounds(0, -20, 100, 20));

    [Fact]
    public void Generate_TargetEqualsDirectLength_ReturnsDirectStraight()
    {
        var result = new MeanderPathGenerator().Generate(AlignedRequest(target: 100));

        result.IsSuccess.ShouldBeTrue();
        result.FailureReason.ShouldBeNull();
        var path = result.Path!;
        path.Segments.Count.ShouldBe(1);
        path.Segments[0].ShouldBeOfType<StraightSegment>();
        path.TotalLengthMicrometers.ShouldBe(100.0, AssertSlack);
    }

    [Fact]
    public void Generate_TargetWithinToleranceOfDirect_ReturnsNearDirectPath()
    {
        var result = new MeanderPathGenerator().Generate(AlignedRequest(target: 100.3));

        result.IsSuccess.ShouldBeTrue();
        result.Path!.Segments.Count.ShouldBe(1);
        result.Path.TotalLengthMicrometers.ShouldBe(100.0, AssertSlack);
    }

    [Fact]
    public void Generate_TargetShorterThanDirect_ReturnsTypedFailureWithoutPath()
    {
        var result = new MeanderPathGenerator().Generate(AlignedRequest(target: 50));

        result.IsSuccess.ShouldBeFalse();
        result.Path.ShouldBeNull();
        result.FailureReason.ShouldBe(MeanderFailureReason.TargetShorterThanDirectPath);
    }

    [Fact]
    public void Generate_MeanderRequired_HitsTargetLengthWithinTolerance()
    {
        var request = AlignedRequest(target: 250);
        var result = new MeanderPathGenerator().Generate(request);

        result.IsSuccess.ShouldBeTrue(result.FailureMessage);
        var path = result.Path!;
        Math.Abs(path.TotalLengthMicrometers - 250).ShouldBeLessThanOrEqualTo(request.ToleranceMicrometers);
        path.Segments.OfType<BendSegment>().ShouldNotBeEmpty();
    }

    [Fact]
    public void Generate_MeanderRequired_EveryBendRespectsMinRadius()
    {
        var request = AlignedRequest(target: 250);
        var path = new MeanderPathGenerator().Generate(request).Path!;

        foreach (var bend in path.Segments.OfType<BendSegment>())
        {
            bend.RadiusMicrometers.ShouldBeGreaterThanOrEqualTo(request.MinBendRadiusMicrometers);
            Math.Abs(bend.SweepAngleDegrees).ShouldBeLessThanOrEqualTo(90.0 + AssertSlack);
        }
    }

    [Fact]
    public void Generate_MeanderRequired_StaysFullyInsideBounds()
    {
        var request = AlignedRequest(target: 250);
        var path = new MeanderPathGenerator().Generate(request).Path!;

        foreach (var segment in path.Segments)
        {
            var b = PathSegmentBounds.Of(segment);
            b.MinX.ShouldBeGreaterThanOrEqualTo(request.Bounds.MinX - AssertSlack);
            b.MinY.ShouldBeGreaterThanOrEqualTo(request.Bounds.MinY - AssertSlack);
            b.MaxX.ShouldBeLessThanOrEqualTo(request.Bounds.MaxX + AssertSlack);
            b.MaxY.ShouldBeLessThanOrEqualTo(request.Bounds.MaxY + AssertSlack);
        }
    }

    [Fact]
    public void Generate_MeanderRequired_PreservesEndpointPosesAndContinuity()
    {
        var path = new MeanderPathGenerator().Generate(AlignedRequest(target: 250)).Path!;

        path.IsValid.ShouldBeTrue();
        path.Segments[0].StartPoint.X.ShouldBe(0.0, AssertSlack);
        path.Segments[0].StartPoint.Y.ShouldBe(0.0, AssertSlack);
        AngleDistance(path.Segments[0].StartAngleDegrees, 0.0).ShouldBeLessThanOrEqualTo(AssertSlack);
        path.Segments[^1].EndPoint.X.ShouldBe(100.0, AssertSlack);
        path.Segments[^1].EndPoint.Y.ShouldBe(0.0, AssertSlack);
        AngleDistance(path.Segments[^1].EndAngleDegrees, 0.0).ShouldBeLessThanOrEqualTo(AssertSlack);
    }

    private static double AngleDistance(double aDeg, double bDeg)
    {
        double delta = (aDeg - bDeg) % 360.0;
        if (delta < 0.0)
            delta += 360.0;
        return Math.Min(delta, 360.0 - delta);
    }

    [Fact]
    public void Generate_SmallExtraLength_UsesDoubleSBendAndHitsTarget()
    {
        // Extra 3 µm < (2π-4)·r = 11.4 µm: too small for even one hairpin loop.
        var request = AlignedRequest(target: 103);
        var result = new MeanderPathGenerator().Generate(request);

        result.IsSuccess.ShouldBeTrue(result.FailureMessage);
        Math.Abs(result.Path!.TotalLengthMicrometers - 103)
            .ShouldBeLessThanOrEqualTo(request.ToleranceMicrometers);
    }

    [Fact]
    public void Generate_MediumExtraLength_UsesDoubleSBendNear90DegreesAndHitsTarget()
    {
        // Extra 10 µm is just below the first hairpin loop at (2π-4)·r = 11.4 µm,
        // so the double S-bend sweep is close to its 90° ceiling.
        var request = AlignedRequest(target: 110);
        var result = new MeanderPathGenerator().Generate(request);

        result.IsSuccess.ShouldBeTrue(result.FailureMessage);
        Math.Abs(result.Path!.TotalLengthMicrometers - 110)
            .ShouldBeLessThanOrEqualTo(request.ToleranceMicrometers);
    }

    [Fact]
    public void Generate_BoundsTooNarrowForMeander_ReturnsTypedFailureWithoutPath()
    {
        var bounds = new MeanderBounds(0, -0.5, 100, 0.5);
        var result = new MeanderPathGenerator().Generate(AlignedRequest(target: 250, bounds: bounds));

        result.IsSuccess.ShouldBeFalse();
        result.Path.ShouldBeNull();
        result.FailureReason.ShouldBe(MeanderFailureReason.BoundsTooSmallForMeander);
    }

    [Fact]
    public void Generate_BoundsExcludeDirectRoute_ReturnsTypedFailureWithoutPath()
    {
        var bounds = new MeanderBounds(10, -20, 100, 20);
        var result = new MeanderPathGenerator().Generate(AlignedRequest(target: 100, bounds: bounds));

        result.IsSuccess.ShouldBeFalse();
        result.Path.ShouldBeNull();
        result.FailureReason.ShouldBe(MeanderFailureReason.BoundsTooSmallForMeander);
    }

    [Fact]
    public void Generate_OffsetParallelPins_MeandersOnSmoothNominalRoute()
    {
        double nominalLength = NominalRouteLength(0, 0, 0, 100, 20, 0, radius: 5.0);
        var request = new MeanderRequest(0, 0, 0, 100, 20, 0,
            nominalLength + 40, 0.5, 5.0, new MeanderBounds(-50, -50, 150, 70));

        var result = new MeanderPathGenerator().Generate(request);

        result.IsSuccess.ShouldBeTrue(result.FailureMessage);
        var path = result.Path!;
        path.IsValid.ShouldBeTrue();
        Math.Abs(path.TotalLengthMicrometers - request.TargetLengthMicrometers)
            .ShouldBeLessThanOrEqualTo(request.ToleranceMicrometers);
        path.Segments[^1].EndPoint.X.ShouldBe(100.0, AssertSlack);
        path.Segments[^1].EndPoint.Y.ShouldBe(20.0, AssertSlack);
    }

    [Fact]
    public void Generate_PerpendicularPins_MeandersOnSmoothNominalRoute()
    {
        double nominalLength = NominalRouteLength(0, 0, 0, 200, -100, 270, radius: 5.0);
        var request = new MeanderRequest(0, 0, 0, 200, -100, 270,
            nominalLength + 30, 0.5, 5.0, new MeanderBounds(-50, -150, 250, 50));

        var result = new MeanderPathGenerator().Generate(request);

        result.IsSuccess.ShouldBeTrue(result.FailureMessage);
        var path = result.Path!;
        path.IsValid.ShouldBeTrue();
        Math.Abs(path.TotalLengthMicrometers - request.TargetLengthMicrometers)
            .ShouldBeLessThanOrEqualTo(request.ToleranceMicrometers);
        path.Segments[^1].EndAngleDegrees.ShouldBe(270.0, AssertSlack);
    }

    [Fact]
    public void Generate_CoincidentPosesZeroTarget_ReturnsEmptyPath()
    {
        var request = new MeanderRequest(10, 10, 90, 10, 10, 90,
            0, 0.5, 5.0, new MeanderBounds(0, 0, 20, 20));

        var result = new MeanderPathGenerator().Generate(request);

        result.IsSuccess.ShouldBeTrue(result.FailureMessage);
        result.Path!.TotalLengthMicrometers.ShouldBe(0.0, AssertSlack);
    }

    [Fact]
    public void Generate_SameInputsTwice_ProducesIdenticalPath()
    {
        var request = AlignedRequest(target: 250);
        var generator = new MeanderPathGenerator();

        var first = generator.Generate(request).Path!;
        var second = generator.Generate(request).Path!;

        second.Segments.Count.ShouldBe(first.Segments.Count);
        for (int i = 0; i < first.Segments.Count; i++)
        {
            var a = first.Segments[i];
            var b = second.Segments[i];
            b.GetType().ShouldBe(a.GetType());
            b.StartPoint.ShouldBe(a.StartPoint);
            b.EndPoint.ShouldBe(a.EndPoint);
            b.StartAngleDegrees.ShouldBe(a.StartAngleDegrees);
            b.EndAngleDegrees.ShouldBe(a.EndAngleDegrees);
            if (a is BendSegment bendA)
            {
                var bendB = (BendSegment)b;
                bendB.Center.ShouldBe(bendA.Center);
                bendB.RadiusMicrometers.ShouldBe(bendA.RadiusMicrometers);
                bendB.SweepAngleDegrees.ShouldBe(bendA.SweepAngleDegrees);
            }
        }
    }

    [Fact]
    public void Generate_NonPositiveRadius_Throws()
    {
        Should.Throw<ArgumentException>(
            () => new MeanderPathGenerator().Generate(AlignedRequest(target: 100, radius: 0)));
    }

    private static double NominalRouteLength(
        double startX, double startY, double startAngle,
        double endX, double endY, double endAngle, double radius)
    {
        var path = new RoutedPath();
        new ManhattanRouter(radius).Route(startX, startY, startAngle, endX, endY, endAngle, path);
        return path.TotalLengthMicrometers;
    }
}
