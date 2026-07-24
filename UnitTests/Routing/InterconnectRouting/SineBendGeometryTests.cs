using CAP_Core.Routing;
using CAP_Core.Routing.InterconnectRouting;
using Shouldly;
using Xunit;

namespace UnitTests.Routing.InterconnectRouting;

/// <summary>
/// Verifies the sine S-bend polyline (<see cref="SineBendGeometry"/>): exact endpoints,
/// parallel arrival, the nd.sinebend curve law, smoothness and well-formed segments.
/// </summary>
public class SineBendGeometryTests
{
    private const double Longitudinal = 200.0;
    private const double Lateral = 80.0;

    [Fact]
    public void Build_EndsExactlyAtTheOffsetTarget()
    {
        var segments = SineBendGeometry.Build(10, 20, 0, Longitudinal, Lateral)!;

        segments[0].StartPoint.X.ShouldBe(10.0, 1e-9);
        segments[0].StartPoint.Y.ShouldBe(20.0, 1e-9);
        segments[^1].EndPoint.X.ShouldBe(10.0 + Longitudinal, 1e-9);
        segments[^1].EndPoint.Y.ShouldBe(20.0 + Lateral, 1e-9);
    }

    [Fact]
    public void Build_StartsAndArrivesParallelToTheHeading()
    {
        var segments = SineBendGeometry.Build(0, 0, 0, Longitudinal, Lateral)!;

        // y'(0) = y'(distance) = 0 for the sine bend; the first/last chord may only deviate
        // by the half-chord sampling error.
        Math.Abs(segments[0].StartAngleDegrees).ShouldBeLessThan(3.0);
        Math.Abs(segments[^1].EndAngleDegrees).ShouldBeLessThan(3.0);
    }

    [Fact]
    public void Build_FollowsTheSinebendCurveLaw()
    {
        // Heading 0 keeps local == world: every sample must satisfy
        // y(x) = offset·(x/dist − sin(2π·x/dist)/(2π)).
        var segments = SineBendGeometry.Build(0, 0, 0, Longitudinal, Lateral)!;

        foreach (var segment in segments)
        {
            double u = segment.EndPoint.X / Longitudinal;
            double expectedY = Lateral * (u - Math.Sin(2 * Math.PI * u) / (2 * Math.PI));
            segment.EndPoint.Y.ShouldBe(expectedY, 1e-9);
        }
    }

    [Fact]
    public void Build_ProducesTheConfiguredChordCount_AllFiniteAndNonZero()
    {
        var segments = SineBendGeometry.Build(0, 0, 0, Longitudinal, Lateral)!;

        segments.Count.ShouldBe(SineBendGeometry.SampleCount);
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
        var segments = SineBendGeometry.Build(0, 0, 0, Longitudinal, Lateral)!;

        for (int i = 1; i < segments.Count; i++)
        {
            double turn = Math.Abs(segments[i].StartAngleDegrees - segments[i - 1].EndAngleDegrees);
            turn.ShouldBeLessThan(4.0, $"kink of {turn:F1}° between chords {i - 1} and {i}");
        }
    }

    [Fact]
    public void Build_ProgressesMonotonicallyForward()
    {
        var segments = SineBendGeometry.Build(0, 0, 0, Longitudinal, Lateral)!;

        foreach (var segment in segments)
            segment.EndPoint.X.ShouldBeGreaterThan(segment.StartPoint.X);
    }

    [Fact]
    public void Build_RotatedFrame_EndsAtTheRotatedTarget()
    {
        // Heading 90° (downward in app space): forward = +Y, lateral = −X.
        var segments = SineBendGeometry.Build(0, 0, 90, Longitudinal, Lateral)!;

        segments[^1].EndPoint.X.ShouldBe(-Lateral, 1e-9);
        segments[^1].EndPoint.Y.ShouldBe(Longitudinal, 1e-9);
    }

    [Fact]
    public void Build_NegligibleLateralOffset_DegeneratesToOneExactStraight()
    {
        var segments = SineBendGeometry.Build(5, 5, 0, Longitudinal, 0)!;

        segments.Count.ShouldBe(1);
        segments[0].LengthMicrometers.ShouldBe(Longitudinal, 1e-9);
    }

    [Fact]
    public void Build_EndPinBehindStart_ReturnsNull()
    {
        SineBendGeometry.Build(0, 0, 0, -10, Lateral).ShouldBeNull();
        SineBendGeometry.Build(0, 0, 0, 0, Lateral).ShouldBeNull();
    }
}
