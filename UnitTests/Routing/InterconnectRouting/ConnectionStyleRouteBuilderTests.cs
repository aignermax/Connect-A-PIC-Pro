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
/// Verifies that an explicit routing style reshapes the visible route into the matching
/// primitive geometry, that Auto keeps the collision-avoiding A* route, and that the built
/// canvas geometry shares its basis (distance / radius / turn) with the Nazca exporter.
/// </summary>
public class ConnectionStyleRouteBuilderTests
{
    private const double Radius = 10.0;

    [Fact]
    public void Straight_ProducesSingleStraightSegmentOfPinDistance()
    {
        var conn = CreateConnection(WaveguideType.Straight);

        var path = ConnectionStyleRouteBuilder.Build(conn.StartPin, conn.EndPin, conn.Type, Radius);

        path.Segments.Count.ShouldBe(1);
        path.Segments[0].ShouldBeOfType<StraightSegment>();
        path.Segments[0].LengthMicrometers.ShouldBe(50.0, 0.01);
    }

    [Theory]
    [InlineData(WaveguideType.Bend)]
    [InlineData(WaveguideType.Euler)]
    public void ArcStyles_AngledOffsetPins_BuildStubArcStub_ReachingEndPinExactly(WaveguideType type)
    {
        // End pin at (100, 55) pointing 270° → arrival direction 90°, a 90° turn. The corner of
        // the two pin axes is at (100, 25): 50 µm ahead of the start pin, 30 µm before the end.
        var conn = CreateConnection(type, endOffsetY: 30, endPinAngleDegrees: 270);

        var path = ConnectionStyleRouteBuilder.Build(conn.StartPin, conn.EndPin, conn.Type, Radius);

        // Stub – arc – stub: exactly one arc with the requested radius and the 90° sweep.
        path.Segments.Count.ShouldBe(3);
        path.Segments[0].ShouldBeOfType<StraightSegment>();
        var bend = path.Segments[1].ShouldBeOfType<BendSegment>();
        path.Segments[2].ShouldBeOfType<StraightSegment>();
        bend.RadiusMicrometers.ShouldBe(Radius, 0.01);
        Math.Abs(bend.SweepAngleDegrees).ShouldBe(90.0, 0.01);

        // Both pins are hit exactly (tangent length τ = r·tan(45°) = 10 → stubs 40 µm and 20 µm).
        var (startX, startY) = conn.StartPin.GetAbsolutePosition();
        var (endX, endY) = conn.EndPin.GetAbsolutePosition();
        path.Segments[0].StartPoint.X.ShouldBe(startX, 0.5);
        path.Segments[0].StartPoint.Y.ShouldBe(startY, 0.5);
        path.Segments[^1].EndPoint.X.ShouldBe(endX, 0.5);
        path.Segments[^1].EndPoint.Y.ShouldBe(endY, 0.5);
        path.Segments[0].LengthMicrometers.ShouldBe(40.0, 0.1);
        path.Segments[2].LengthMicrometers.ShouldBe(20.0, 0.1);
    }

    [Fact]
    public void Bend_AngledPins_ArcIsGrabbableByRadiusHandles()
    {
        // The stubs make the arc an interior bend (flanked by straights), so the in-canvas
        // radius handles (GetBendCorners) find it.
        var conn = CreateConnection(WaveguideType.Bend, endOffsetY: 30, endPinAngleDegrees: 270);

        var path = ConnectionStyleRouteBuilder.Build(conn.StartPin, conn.EndPin, conn.Type, Radius);

        var corners = BendRadiusEditor.GetBendCorners(path.Segments);
        corners.Count.ShouldBe(1);
        corners[0].RadiusMicrometers.ShouldBe(Radius, 0.01);
    }

    [Fact]
    public void Bend_RadiusTooLargeForCorner_ClampsRadius_StillReachesEndPin()
    {
        // The corner legs are 50 µm and 30 µm; a 100 µm radius needs τ = 100 µm of tangent —
        // impossible. The radius must be clamped to ~30 µm, never abandoned to a straight line.
        const double requestedRadius = 100.0;
        var conn = CreateConnection(WaveguideType.Bend, endOffsetY: 30, endPinAngleDegrees: 270);

        var path = ConnectionStyleRouteBuilder.Build(conn.StartPin, conn.EndPin, conn.Type, requestedRadius);

        var bend = path.Segments.OfType<BendSegment>().ShouldHaveSingleItem();
        bend.RadiusMicrometers.ShouldBeLessThan(requestedRadius);
        bend.RadiusMicrometers.ShouldBe(30.0, 0.5); // min(t, s) / tan(45°), minus the safety margin

        var (endX, endY) = conn.EndPin.GetAbsolutePosition();
        path.Segments[^1].EndPoint.X.ShouldBe(endX, 0.5);
        path.Segments[^1].EndPoint.Y.ShouldBe(endY, 0.5);
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
    public void SBend_ReachesEndPinExactly()
    {
        var conn = CreateConnection(WaveguideType.SBend, endOffsetY: 20);

        var path = ConnectionStyleRouteBuilder.Build(conn.StartPin, conn.EndPin, conn.Type, Radius);

        var (endX, endY) = conn.EndPin.GetAbsolutePosition();
        var last = path.Segments[^1];
        last.EndPoint.X.ShouldBe(endX, 0.5);
        last.EndPoint.Y.ShouldBe(endY, 0.5);
    }

    [Theory]
    [InlineData(WaveguideType.SBend)]
    [InlineData(WaveguideType.Cobra)]
    public void PointToPoint_OffsetParallelPins_ProducesRealSCurve_NotDiagonalStraight(WaveguideType type)
    {
        // Parallel pins (end pin faces the start, arrival angle 0) with a lateral offset — the
        // layout that used to collapse to a single diagonal straight for everything but Auto.
        var conn = CreateConnection(type, endOffsetY: 20);

        var path = ConnectionStyleRouteBuilder.Build(conn.StartPin, conn.EndPin, conn.Type, Radius);

        // A real S: more than one segment, with two arcs — never a lone diagonal StraightSegment.
        path.Segments.Count.ShouldBeGreaterThan(1);
        path.Segments.OfType<BendSegment>().Count().ShouldBe(2);

        // Reaches the end pin exactly and arrives parallel to the start heading (0°).
        var (endX, endY) = conn.EndPin.GetAbsolutePosition();
        var last = path.Segments[^1];
        last.EndPoint.X.ShouldBe(endX, 0.5);
        last.EndPoint.Y.ShouldBe(endY, 0.5);
        last.EndAngleDegrees.ShouldBe(0.0, 0.5);
    }

    [Fact]
    public void SBend_RadiusTooLargeForOffset_ReducesRadius_StillReachesEndPin()
    {
        // A large lateral offset over a short forward span cannot host the requested 20 µm radius;
        // the builder must shrink the radius rather than fall back to a straight.
        const double requestedRadius = 20.0;
        var conn = CreateConnection(WaveguideType.SBend, endOffsetY: 40);

        var path = ConnectionStyleRouteBuilder.Build(conn.StartPin, conn.EndPin, conn.Type, requestedRadius);

        path.Segments.OfType<BendSegment>().ShouldNotBeEmpty();
        var maxRadius = path.Segments.OfType<BendSegment>().Max(b => b.RadiusMicrometers);
        maxRadius.ShouldBeLessThan(requestedRadius); // reduced to fit the offset
        var (endX, endY) = conn.EndPin.GetAbsolutePosition();
        path.Segments[^1].EndPoint.X.ShouldBe(endX, 0.5);
        path.Segments[^1].EndPoint.Y.ShouldBe(endY, 0.5);
    }

    [Fact]
    public void Bend_ParallelOffsetPins_FallsBackToSCurve_NotDiagonalStraight()
    {
        // A single arc cannot join parallel offset pins; Bend falls back to an S-bend.
        var conn = CreateConnection(WaveguideType.Bend, endOffsetY: 20); // end pin angle 180 → parallel

        var path = ConnectionStyleRouteBuilder.Build(conn.StartPin, conn.EndPin, conn.Type, Radius);

        path.Segments.Count.ShouldBeGreaterThan(1);
        path.Segments.OfType<BendSegment>().Count().ShouldBe(2);
        var (endX, endY) = conn.EndPin.GetAbsolutePosition();
        path.Segments[^1].EndPoint.X.ShouldBe(endX, 0.5);
        path.Segments[^1].EndPoint.Y.ShouldBe(endY, 0.5);
    }

    [Fact]
    public void Straight_OffsetPins_RunsAlongHeading_NotDiagonalToEndPin()
    {
        // Straight is nd.strt along the start heading; it must stay horizontal (heading 0),
        // never a silent diagonal to an offset end pin.
        var conn = CreateConnection(WaveguideType.Straight, endOffsetY: 20);

        var path = ConnectionStyleRouteBuilder.Build(conn.StartPin, conn.EndPin, conn.Type, Radius);

        path.Segments.Count.ShouldBe(1);
        var straight = path.Segments[0].ShouldBeOfType<StraightSegment>();
        straight.StartPoint.Y.ShouldBe(straight.EndPoint.Y, 0.01); // no lateral drift
    }

    [Fact]
    public void ExplicitStyle_RecalculateFreezesStyledGeometry_NotAStar()
    {
        var conn = CreateConnection(WaveguideType.Straight);
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
        // Straight: canvas segment length must equal the exporter's nd.strt(length=...).
        var straight = CreateConnection(WaveguideType.Straight);
        var straightPath = ConnectionStyleRouteBuilder.Build(
            straight.StartPin, straight.EndPin, straight.Type, straight.BendRadiusMicrometers);
        double exportedLength = ParseArg(NazcaConnectionStyleWriter.Format(straight)!, "length");
        straightPath.Segments[0].LengthMicrometers.ShouldBe(exportedLength, 0.05);
    }

    [Theory]
    [InlineData(WaveguideType.Bend)]
    [InlineData(WaveguideType.Euler)]
    public void ArcStyles_HaveNoSinglePrimitiveExport_SegmentsAreTheTruth(WaveguideType type)
    {
        // A lone nd.bend/nd.euler is parameterized by (radius, angle) only and cannot land on an
        // arbitrary end pin. Format therefore returns null and the exporter writes the exact
        // canvas segments instead — canvas and GDS are identical by construction.
        var conn = CreateConnection(type, endOffsetY: 30, endPinAngleDegrees: 270);

        NazcaConnectionStyleWriter.Format(conn).ShouldBeNull();
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
