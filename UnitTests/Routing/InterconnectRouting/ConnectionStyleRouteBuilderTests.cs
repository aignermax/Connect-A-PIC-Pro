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

    [Fact]
    public void Bend_ProducesSingleArcWithGivenRadiusAndTurnMagnitude()
    {
        // End pin points 90° in app space → the waveguide turns by 90°.
        var conn = CreateConnection(WaveguideType.Bend, endPinAngleDegrees: 90);

        var path = ConnectionStyleRouteBuilder.Build(conn.StartPin, conn.EndPin, conn.Type, Radius);

        path.Segments.Count.ShouldBe(1);
        var bend = path.Segments[0].ShouldBeOfType<BendSegment>();
        bend.RadiusMicrometers.ShouldBe(Radius, 0.01);
        Math.Abs(bend.SweepAngleDegrees).ShouldBe(90.0, 0.01);
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

        // Bend: canvas arc radius/turn magnitude must equal the exporter's nd.bend(radius, angle).
        var bendConn = CreateConnection(WaveguideType.Bend, endPinAngleDegrees: 90);
        bendConn.BendRadiusMicrometers = Radius;
        var bendPath = ConnectionStyleRouteBuilder.Build(
            bendConn.StartPin, bendConn.EndPin, bendConn.Type, bendConn.BendRadiusMicrometers);
        var arc = (BendSegment)bendPath.Segments[0];
        string bendLine = NazcaConnectionStyleWriter.Format(bendConn)!;
        arc.RadiusMicrometers.ShouldBe(ParseArg(bendLine, "radius"), 0.01);
        Math.Abs(arc.SweepAngleDegrees).ShouldBe(Math.Abs(ParseArg(bendLine, "angle")), 0.01);
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
