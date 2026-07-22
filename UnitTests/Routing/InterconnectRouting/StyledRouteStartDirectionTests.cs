using CAP_Core.Components;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Routing.InterconnectRouting;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Routing.InterconnectRouting;

/// <summary>
/// Guards the start-direction invariant of <see cref="ConnectionStyleRouteBuilder"/>: every
/// styled route leaves the start pin ALONG the pin's direction — the first segment is
/// tangential to the start heading and never points backwards into the component. Layouts a
/// style cannot cover that way (end pin behind the start pin) must yield null so the caller
/// falls back to the A* route instead of drawing a broken curve.
/// </summary>
public class StyledRouteStartDirectionTests
{
    /// <summary>Bend emits exact straight/arc segments, so its first segment must match the
    /// pin angle to a fraction of a degree.</summary>
    private const double ExactTolerance = 1.0;

    /// <summary>SBend/Cobra are sampled into 48 chords: their analytic tangent at the pin is
    /// exact, but the FIRST CHORD averages the curve over 1/48 of its run, which deviates by
    /// a few degrees in extreme layouts (e.g. cobra looping around a backward end pin).</summary>
    private const double ChordTolerance = 5.0;

    private const double PinTolerance = 0.5;

    public static TheoryData<WaveguideType, double, double, double> AllStylesAndLayouts()
    {
        var data = new TheoryData<WaveguideType, double, double, double>();
        // (endOffsetX, endOffsetY, endPinAngle): forward layouts with small/large ±Y offsets,
        // angled end pins (single-arc case) and end pins BEHIND the start pin.
        var layouts = new (double Dx, double Dy, double Angle)[]
        {
            (100, 0, 180),
            (100, 10, 180), (100, -10, 180),
            (100, 80, 180), (100, -80, 180),
            (100, 200, 180),
            (100, 30, 270), (100, -30, 90),
            (-150, 40, 0), (-150, -40, 0), (-150, 40, 180),
        };
        foreach (var style in new[] { WaveguideType.Bend, WaveguideType.SBend, WaveguideType.Cobra })
            foreach (var (dx, dy, angle) in layouts)
                data.Add(style, dx, dy, angle);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllStylesAndLayouts))]
    public void Build_EitherLeavesPinAlongItsDirection_OrReturnsNullForAStarFallback(
        WaveguideType style, double endOffsetX, double endOffsetY, double endPinAngle)
    {
        var conn = CreateConnection(style, endOffsetX, endOffsetY, endPinAngle);

        var path = ConnectionStyleRouteBuilder.Build(conn.StartPin, conn.EndPin, conn.Type);
        if (path == null)
            return; // no tangential styled curve exists for this layout: A* takes over

        double tolerance = style == WaveguideType.Bend ? ExactTolerance : ChordTolerance;
        double deviation = Math.Abs(NormalizeSigned(path.Segments[0].StartAngleDegrees - 0.0));
        deviation.ShouldBeLessThanOrEqualTo(tolerance,
            $"{style} route must leave the start pin along its 0° heading, " +
            $"but the first segment starts at {path.Segments[0].StartAngleDegrees:F1}°");
        AssertConnectsBothPins(conn, path);
    }

    [Theory]
    [InlineData(WaveguideType.Bend)]
    [InlineData(WaveguideType.SBend)]
    public void Build_EndPinBehindStartPin_ReturnsNull_InsteadOfBackwardStraight(WaveguideType style)
    {
        // Historically this layout produced a raw diagonal leaving the start pin backwards
        // INTO the component (see routing-styles/behind-*.png diagnostics).
        var conn = CreateConnection(style, endOffsetX: -150, endOffsetY: 40, endPinAngleDegrees: 0);

        ConnectionStyleRouteBuilder.Build(conn.StartPin, conn.EndPin, conn.Type).ShouldBeNull();
    }

    [Fact]
    public void Recalculate_StyledConnectionWithEndPinBehind_FallsBackToAStarRoute()
    {
        var conn = CreateConnection(WaveguideType.Bend, endOffsetX: -150, endOffsetY: 40,
            endPinAngleDegrees: 0);

        conn.RecalculateTransmission(new WaveguideRouter());

        conn.RoutedPath.ShouldNotBeNull("the connection must stay visibly routed via A*");
        conn.IsRouteFrozen.ShouldBeFalse("the A* fallback re-routes like Auto");
        conn.Type.ShouldBe(WaveguideType.Bend, "the chosen style is kept for when the layout allows it");
    }

    private static void AssertConnectsBothPins(WaveguideConnection conn, RoutedPath path)
    {
        var (startX, startY) = conn.StartPin.GetAbsolutePosition();
        var (endX, endY) = conn.EndPin.GetAbsolutePosition();
        path.Segments[0].StartPoint.X.ShouldBe(startX, PinTolerance);
        path.Segments[0].StartPoint.Y.ShouldBe(startY, PinTolerance);
        path.Segments[^1].EndPoint.X.ShouldBe(endX, PinTolerance);
        path.Segments[^1].EndPoint.Y.ShouldBe(endY, PinTolerance);
    }

    private static double NormalizeSigned(double angleDegrees)
    {
        double a = angleDegrees % 360.0;
        if (a > 180.0) a -= 360.0;
        if (a <= -180.0) a += 360.0;
        return a;
    }

    /// <summary>Start pin at app (50, 25) pointing 0°; end pin at
    /// (50 + endOffsetX, 25 + endOffsetY) pointing <paramref name="endPinAngleDegrees"/>.</summary>
    private static WaveguideConnection CreateConnection(
        WaveguideType type, double endOffsetX, double endOffsetY, double endPinAngleDegrees)
    {
        var startComponent = CreateTestComponent(0, 0);
        var endComponent = CreateTestComponent(50 + endOffsetX, endOffsetY);

        return new WaveguideConnection
        {
            Type = type,
            StartPin = new PhysicalPin
            {
                Name = "output",
                OffsetXMicrometers = 50,
                OffsetYMicrometers = 25,
                AngleDegrees = 0,
                ParentComponent = startComponent,
            },
            EndPin = new PhysicalPin
            {
                Name = "input",
                OffsetXMicrometers = 0,
                OffsetYMicrometers = 25,
                AngleDegrees = endPinAngleDegrees,
                ParentComponent = endComponent,
            },
        };
    }

    private static Component CreateTestComponent(double x, double y)
    {
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin>());

        return new Component(
            laserWaveLengthToSMatrixMap: new Dictionary<int, SMatrix>(),
            sliders: new List<Slider>(),
            nazcaFunctionName: "test",
            nazcaFunctionParams: "",
            parts: parts,
            typeNumber: 0,
            identifier: $"TestComponent_{x}_{y}",
            rotationCounterClock: DiscreteRotation.R0)
        {
            WidthMicrometers = 50,
            HeightMicrometers = 50,
            PhysicalX = x,
            PhysicalY = y,
        };
    }
}
