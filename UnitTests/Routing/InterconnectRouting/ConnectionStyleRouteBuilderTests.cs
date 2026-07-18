using System.Globalization;
using System.Text.RegularExpressions;
using CAP_Core.Components;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Export.InterconnectRouting;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Routing.InterconnectRouting;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Routing.InterconnectRouting;

/// <summary>
/// Verifies that each explicit routing style produces a visibly distinct, smooth curve that
/// reaches BOTH pins exactly, that Auto keeps the collision-avoiding A* route, and that the
/// built canvas geometry shares its basis with the Nazca exporter.
/// </summary>
public class ConnectionStyleRouteBuilderTests
{
    private const double PinTolerance = 0.5;

    [Fact]
    public void Bend_AlignedFacingPins_ProducesSingleStraightSegmentOfPinDistance()
    {
        // Collinear facing pins have a 0° turn — no arc exists, and the degenerate S-bend
        // collapses to the exact pin-to-pin straight.
        var conn = CreateConnection(WaveguideType.Bend);

        var path = ConnectionStyleRouteBuilder.Build(conn.StartPin, conn.EndPin, conn.Type);

        path.Segments.Count.ShouldBe(1);
        path.Segments[0].ShouldBeOfType<StraightSegment>();
        path.Segments[0].LengthMicrometers.ShouldBe(50.0, 0.01);
    }

    [Fact]
    public void Bend_AngledPins_BuildsStubArcStub_WithGenerousRadius()
    {
        // End pin at (100, 55) pointing 270° → arrival direction 90°, a 90° turn. The corner of
        // the two pin axes is at (100, 25): legs 50 µm (start) and 30 µm (end). The largest
        // fitting radius is min(50, 30)/tan(45°) = 30 µm; the builder uses 0.9 × that = 27 µm,
        // leaving straight stubs of 50−27 = 23 µm and 30−27 = 3 µm.
        var conn = CreateConnection(WaveguideType.Bend, endOffsetY: 30, endPinAngleDegrees: 270);

        var path = ConnectionStyleRouteBuilder.Build(conn.StartPin, conn.EndPin, conn.Type);

        path.Segments.Count.ShouldBe(3);
        path.Segments[0].ShouldBeOfType<StraightSegment>();
        var bend = path.Segments[1].ShouldBeOfType<BendSegment>();
        path.Segments[2].ShouldBeOfType<StraightSegment>();
        bend.RadiusMicrometers.ShouldBe(30.0 * SBendGeometry.GenerousRadiusFactor, 0.01);
        Math.Abs(bend.SweepAngleDegrees).ShouldBe(90.0, 0.01);
        path.Segments[0].LengthMicrometers.ShouldBe(23.0, 0.1);
        path.Segments[2].LengthMicrometers.ShouldBe(3.0, 0.1);
        AssertConnectsBothPins(conn, path);
    }

    [Fact]
    public void Bend_AngledPins_ArcIsGrabbableByRadiusHandles()
    {
        // The stubs make the arc an interior bend (flanked by straights), so the in-canvas
        // radius handles (GetBendCorners) find it.
        var conn = CreateConnection(WaveguideType.Bend, endOffsetY: 30, endPinAngleDegrees: 270);

        var path = ConnectionStyleRouteBuilder.Build(conn.StartPin, conn.EndPin, conn.Type);

        var corners = BendRadiusEditor.GetBendCorners(path.Segments);
        corners.Count.ShouldBe(1);
        corners[0].RadiusMicrometers.ShouldBe(30.0 * SBendGeometry.GenerousRadiusFactor, 0.01);
    }

    [Fact]
    public void Bend_AngledPins_RecalculatedRouteStaysValid_NoRebuildChurn()
    {
        // Incremental routing keeps a frozen route only while FrozenPathStillMatchesPins():
        // since the stub–arc–stub reaches both pins, a styled Bend must satisfy it — otherwise
        // WaveguideConnectionManager.IsRouteStillValid would reject and rebuild it on every pass.
        var conn = CreateConnection(WaveguideType.Bend, endOffsetY: 30, endPinAngleDegrees: 270);

        conn.RecalculateTransmission(new WaveguideRouter());

        conn.IsRouteFrozen.ShouldBeTrue();
        conn.FrozenPathStillMatchesPins().ShouldBeTrue();
    }

    [Fact]
    public void Bend_ParallelOffsetPins_BuildsGenerousTwoArcS_WithHandles()
    {
        // A single arc cannot join parallel offset pins; Bend builds the two-arc S with the
        // generous radius: 0.9 × the largest fitting radius for the inner span, and the arcs
        // stay interior (flanked by straights) so the radius handles can grab them.
        var conn = CreateConnection(WaveguideType.Bend, endOffsetY: 20); // end pin angle 180 → parallel

        var path = ConnectionStyleRouteBuilder.Build(conn.StartPin, conn.EndPin, conn.Type);

        var bends = path.Segments.OfType<BendSegment>().ToList();
        bends.Count.ShouldBe(2);
        bends[0].RadiusMicrometers.ShouldBe(
            MaxTwoArcRadius(longitudinal: 50, lateral: 20) * SBendGeometry.GenerousRadiusFactor, 0.05);
        BendRadiusEditor.GetBendCorners(path.Segments).Count.ShouldBe(2);
        AssertConnectsBothPins(conn, path);
    }

    [Fact]
    public void SBend_OffsetParallelPins_ProducesSmoothSinePolyline()
    {
        var conn = CreateConnection(WaveguideType.SBend, endOffsetY: 20);

        var path = ConnectionStyleRouteBuilder.Build(conn.StartPin, conn.EndPin, conn.Type);

        // A pure polyline: many straight chords, no BendSegments (a smooth sine has no single
        // radius — correctly, no radius handles either).
        path.Segments.Count.ShouldBe(SineBendGeometry.SampleCount);
        path.Segments.ShouldAllBe(s => s is StraightSegment);
        BendRadiusEditor.GetBendCorners(path.Segments).ShouldBeEmpty();

        // Arrives at the end pin exactly and parallel to the start heading.
        AssertConnectsBothPins(conn, path);
        NormalizeSigned(path.Segments[^1].EndAngleDegrees).ShouldBe(0.0, 3.0);
        AssertSmooth(path.Segments);
    }

    [Fact]
    public void Cobra_OffsetParallelPins_ProducesSmoothHermitePolyline_DistinctFromSine()
    {
        var conn = CreateConnection(WaveguideType.Cobra, endOffsetY: 20);

        var cobra = ConnectionStyleRouteBuilder.Build(conn.StartPin, conn.EndPin, WaveguideType.Cobra);
        var sine = ConnectionStyleRouteBuilder.Build(conn.StartPin, conn.EndPin, WaveguideType.SBend);

        cobra.Segments.Count.ShouldBe(CobraGeometry.SampleCount);
        cobra.Segments.ShouldAllBe(s => s is StraightSegment);
        AssertConnectsBothPins(conn, cobra);
        AssertSmooth(cobra.Segments);

        // The cobra (Hermite) curve is a different shape than the sine bend: away from the
        // midpoint their lateral profiles must visibly diverge.
        double maxDeviation = 0;
        for (int i = 0; i < cobra.Segments.Count; i++)
        {
            double dy = Math.Abs(cobra.Segments[i].EndPoint.Y - sine.Segments[i].EndPoint.Y);
            maxDeviation = Math.Max(maxDeviation, dy);
        }
        maxDeviation.ShouldBeGreaterThan(0.5, "cobra and sine bend must be visibly different curves");
    }

    [Fact]
    public void SBend_ReachesEndPinExactly()
    {
        var conn = CreateConnection(WaveguideType.SBend, endOffsetY: 20);

        var path = ConnectionStyleRouteBuilder.Build(conn.StartPin, conn.EndPin, conn.Type);

        AssertConnectsBothPins(conn, path);
    }

    [Fact]
    public void Cobra_AngledPins_HonorsArrivalAngle()
    {
        // End pin pointing 270° → the curve must arrive heading 90° — something the sine
        // bend (always parallel arrival) cannot do; this is cobra's distinguishing contract.
        var conn = CreateConnection(WaveguideType.Cobra, endOffsetY: 30, endPinAngleDegrees: 270);

        var path = ConnectionStyleRouteBuilder.Build(conn.StartPin, conn.EndPin, conn.Type);

        AssertConnectsBothPins(conn, path);
        NormalizeSigned(path.Segments[^1].EndAngleDegrees - 90.0).ShouldBe(0.0, 4.0);
    }

    [Fact]
    public void ExplicitStyle_RecalculateFreezesStyledGeometry_NotAStar()
    {
        var conn = CreateConnection(WaveguideType.Bend);
        var router = new WaveguideRouter();

        conn.RecalculateTransmission(router);

        conn.IsRouteFrozen.ShouldBeTrue();
        conn.RoutedPath.ShouldNotBeNull();
        conn.RoutedPath!.Segments.Count.ShouldBe(1);
        conn.RoutedPath.Segments[0].ShouldBeOfType<StraightSegment>();
    }

    [Fact]
    public void Auto_RecalculateUsesRouter_AndDoesNotFreeze()
    {
        var conn = CreateConnection(WaveguideType.Auto);
        var router = new WaveguideRouter();

        conn.RecalculateTransmission(router);

        conn.IsRouteFrozen.ShouldBeFalse();
        conn.RoutedPath.ShouldNotBeNull();
    }

    [Fact]
    public void StyledGeometry_SharesBasisWithNazcaExport()
    {
        // SBend: the canvas sine polyline and the exporter's nd.sinebend(distance=…) must use
        // the same forward run, so canvas and GDS share their curve basis.
        var sBend = CreateConnection(WaveguideType.SBend, endOffsetY: 20);
        var sBendPath = ConnectionStyleRouteBuilder.Build(sBend.StartPin, sBend.EndPin, sBend.Type);
        double exportedDistance = ParseArg(NazcaConnectionStyleWriter.Format(sBend)!, "distance");
        double canvasForwardRun = sBendPath.Segments[^1].EndPoint.X - sBendPath.Segments[0].StartPoint.X;
        canvasForwardRun.ShouldBe(exportedDistance, 0.05);
    }

    [Fact]
    public void Bend_HasNoSinglePrimitiveExport_SegmentsAreTheTruth()
    {
        // A lone nd.bend is parameterized by (radius, angle) only and cannot land on an
        // arbitrary end pin. Format therefore returns null and the exporter writes the exact
        // canvas segments instead — canvas and GDS are identical by construction.
        var conn = CreateConnection(WaveguideType.Bend, endOffsetY: 30, endPinAngleDegrees: 270);

        NazcaConnectionStyleWriter.Format(conn).ShouldBeNull();
    }

    /// <summary>Route must start at the start pin and end at the end pin (±0.5 µm).</summary>
    private static void AssertConnectsBothPins(WaveguideConnection conn, RoutedPath path)
    {
        var (startX, startY) = conn.StartPin.GetAbsolutePosition();
        var (endX, endY) = conn.EndPin.GetAbsolutePosition();
        path.Segments[0].StartPoint.X.ShouldBe(startX, PinTolerance);
        path.Segments[0].StartPoint.Y.ShouldBe(startY, PinTolerance);
        path.Segments[^1].EndPoint.X.ShouldBe(endX, PinTolerance);
        path.Segments[^1].EndPoint.Y.ShouldBe(endY, PinTolerance);
    }

    /// <summary>Consecutive polyline chords may only turn by a few degrees — no kinks.</summary>
    private static void AssertSmooth(IReadOnlyList<PathSegment> segments)
    {
        for (int i = 1; i < segments.Count; i++)
        {
            double turn = Math.Abs(NormalizeSigned(
                segments[i].StartAngleDegrees - segments[i - 1].EndAngleDegrees));
            turn.ShouldBeLessThan(6.0, $"kink of {turn:F1}° between chords {i - 1} and {i}");
        }
    }

    /// <summary>Largest two-arc-S radius for the inner span (mirrors <see cref="SBendGeometry"/>:
    /// 20% stubs each side, max R = inner / (2·sin φ0) with φ0 = 2·atan2(|lateral|, inner)).</summary>
    private static double MaxTwoArcRadius(double longitudinal, double lateral)
    {
        double inner = longitudinal * 0.6;
        double phi0 = 2.0 * Math.Atan2(Math.Abs(lateral), inner);
        return inner / (2.0 * Math.Sin(phi0));
    }

    private static double NormalizeSigned(double angleDegrees)
    {
        double a = angleDegrees % 360.0;
        if (a > 180.0) a -= 360.0;
        if (a <= -180.0) a += 360.0;
        return a;
    }

    private static double ParseArg(string line, string name)
    {
        var match = Regex.Match(line, name + @"=(-?\d+(?:\.\d+)?)");
        match.Success.ShouldBeTrue($"'{name}' not found in: {line}");
        return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Start pin at app (50, 25) pointing 0°; end pin at app (100, 25 + endOffsetY)
    /// pointing <paramref name="endPinAngleDegrees"/> (mirrors the exporter's test fixture).
    /// </summary>
    private static WaveguideConnection CreateConnection(
        WaveguideType type, double endOffsetY = 0, double endPinAngleDegrees = 180)
    {
        var startComponent = CreateTestComponent(0, 0);
        var endComponent = CreateTestComponent(100, endOffsetY);

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
