using CAP_Core.Routing;
using Shouldly;
using Xunit;

namespace UnitTests.Routing;

/// <summary>
/// Tests for <see cref="PathSegmentBounds"/>: arcs must contribute their swept
/// extent only — the full-circle superset inflated group bounds and collision
/// footprints far past the geometry (field report: group move blocked by a
/// "collision region ~5× the group").
/// </summary>
public class PathSegmentBoundsTests
{
    private const double Tol = 1e-9;

    [Fact]
    public void Straight_IsExact()
    {
        var b = PathSegmentBounds.Of(new StraightSegment(10, 20, 50, 30, 0));
        b.MinX.ShouldBe(10, Tol);
        b.MinY.ShouldBe(20, Tol);
        b.MaxX.ShouldBe(50, Tol);
        b.MaxY.ShouldBe(30, Tol);
    }

    [Fact]
    public void QuarterArc_UsesSweptExtentNotFullCircle()
    {
        // 90° arc, center (500,400), radius 100, tangent from 0° to 90°:
        // radial sweep 270°→360°, so the arc occupies x∈[500,600], y∈[300,400] —
        // the full-circle box would claim x∈[400,600], y∈[300,500].
        var bend = new BendSegment(centerX: 500, centerY: 400, radius: 100,
            startAngle: 0, sweepAngle: 90);

        var b = PathSegmentBounds.Of(bend);

        b.MinX.ShouldBe(500, 1e-6);
        b.MinY.ShouldBe(300, 1e-6);
        b.MaxX.ShouldBe(600, 1e-6);
        b.MaxY.ShouldBe(400, 1e-6);
    }

    [Fact]
    public void ArcCrossingCardinal_IncludesTheExtremePoint()
    {
        // 90° arc sweeping radially 0°→90° (tangent 90°→180°): the arc touches
        // (cx+r, cy) and (cx, cy+r) and bulges through the 45° diagonal — but the
        // bbox extremes are the two cardinal endpoints themselves.
        var bend = new BendSegment(centerX: 0, centerY: 0, radius: 50,
            startAngle: 90, sweepAngle: 90);

        var b = PathSegmentBounds.Of(bend);

        b.MinX.ShouldBe(0, 1e-6);
        b.MinY.ShouldBe(0, 1e-6);
        b.MaxX.ShouldBe(50, 1e-6);
        b.MaxY.ShouldBe(50, 1e-6);
    }

    [Fact]
    public void ClockwiseArc_SweepsTheOtherSide()
    {
        // Clockwise 90° arc from tangent 0°: radial sweep 90°→0° downward.
        var bend = new BendSegment(centerX: 500, centerY: 400, radius: 100,
            startAngle: 0, sweepAngle: -90);

        var b = PathSegmentBounds.Of(bend);

        // Start point (600,400), end point (500,500): extent x∈[500,600], y∈[400,500].
        b.MinX.ShouldBe(500, 1e-6);
        b.MinY.ShouldBe(400, 1e-6);
        b.MaxX.ShouldBe(600, 1e-6);
        b.MaxY.ShouldBe(500, 1e-6);
    }

    [Fact]
    public void FullCircle_IncludesAllExtremes()
    {
        var bend = new BendSegment(centerX: 10, centerY: -20, radius: 25,
            startAngle: 0, sweepAngle: 360);

        var b = PathSegmentBounds.Of(bend);

        b.MinX.ShouldBe(-15, 1e-6);
        b.MinY.ShouldBe(-45, 1e-6);
        b.MaxX.ShouldBe(35, 1e-6);
        b.MaxY.ShouldBe(5, 1e-6);
    }

    [Fact]
    public void Padding_ExpandsEverySide()
    {
        var b = PathSegmentBounds.Of(new StraightSegment(0, 0, 10, 0, 0), paddingUm: 2);
        b.MinX.ShouldBe(-2, Tol);
        b.MinY.ShouldBe(-2, Tol);
        b.MaxX.ShouldBe(12, Tol);
        b.MaxY.ShouldBe(2, Tol);
    }
}
