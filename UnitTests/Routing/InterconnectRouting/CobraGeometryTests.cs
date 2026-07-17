using CAP_Core.Routing;
using CAP_Core.Routing.InterconnectRouting;
using Shouldly;
using Xunit;

namespace UnitTests.Routing.InterconnectRouting;

/// <summary>
/// Verifies the cobra (cubic Hermite) polyline (<see cref="CobraGeometry"/>): exact endpoints,
/// correct departure/arrival angles at BOTH ends, smoothness and well-formed segments.
/// </summary>
public class CobraGeometryTests
{
    [Fact]
    public void Build_EndsExactlyAtTheTarget()
    {
        var segments = CobraGeometry.Build(10, 20, 0, 210, 100, 0)!;

        segments[0].StartPoint.X.ShouldBe(10.0, 1e-9);
        segments[0].StartPoint.Y.ShouldBe(20.0, 1e-9);
        segments[^1].EndPoint.X.ShouldBe(210.0, 1e-9);
        segments[^1].EndPoint.Y.ShouldBe(100.0, 1e-9);
    }

    [Theory]
    [InlineData(0.0, 0.0)]     // parallel pins
    [InlineData(0.0, 90.0)]    // 90° arrival — impossible for a sine bend, cobra's specialty
    [InlineData(45.0, -45.0)]  // both ends angled
    public void Build_HonorsDepartureAndArrivalAngles(double startAngle, double arrivalAngle)
    {
        var segments = CobraGeometry.Build(0, 0, startAngle, 150, 60, arrivalAngle)!;

        // First/last chord may only deviate from the analytic tangents by the sampling error.
        NormalizeSigned(segments[0].StartAngleDegrees - startAngle).ShouldBe(0.0, 4.0);
        NormalizeSigned(segments[^1].EndAngleDegrees - arrivalAngle).ShouldBe(0.0, 4.0);
    }

    [Fact]
    public void Build_ProducesTheConfiguredChordCount_AllFiniteAndNonZero()
    {
        var segments = CobraGeometry.Build(0, 0, 0, 200, 80, 0)!;

        segments.Count.ShouldBe(CobraGeometry.SampleCount);
        foreach (var segment in segments)
        {
            segment.ShouldBeOfType<StraightSegment>();
            segment.LengthMicrometers.ShouldBeGreaterThan(0);
            double.IsFinite(segment.StartPoint.X).ShouldBeTrue();
            double.IsFinite(segment.EndPoint.Y).ShouldBeTrue();
        }
    }

    [Fact]
    public void Build_IsSmooth_NoKinksBetweenChords()
    {
        var segments = CobraGeometry.Build(0, 0, 0, 200, 80, 90)!;

        for (int i = 1; i < segments.Count; i++)
        {
            double turn = Math.Abs(NormalizeSigned(
                segments[i].StartAngleDegrees - segments[i - 1].EndAngleDegrees));
            turn.ShouldBeLessThan(6.0, $"kink of {turn:F1}° between chords {i - 1} and {i}");
        }
    }

    [Fact]
    public void Build_CoincidentPins_ReturnsNull()
    {
        CobraGeometry.Build(50, 50, 0, 50, 50, 180).ShouldBeNull();
    }

    private static double NormalizeSigned(double angleDegrees)
    {
        double a = angleDegrees % 360.0;
        if (a > 180.0) a -= 360.0;
        if (a <= -180.0) a += 360.0;
        return a;
    }
}
