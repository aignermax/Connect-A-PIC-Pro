using CAP_Core.Components;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Export.InterconnectRouting;
using CAP_Core.LightCalculation;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Export.InterconnectRouting;

public class NazcaConnectionStyleWriterTests
{
    [Fact]
    public void Format_AutoStyle_ReturnsNull()
    {
        var conn = CreateConnection(WaveguideType.Auto);

        NazcaConnectionStyleWriter.Format(conn).ShouldBeNull();
    }

    [Fact]
    public void Format_SBend_EmitsSinebendWithLocalDistanceAndOffset()
    {
        var conn = CreateConnection(WaveguideType.SBend, endOffsetY: 20);

        var line = NazcaConnectionStyleWriter.Format(conn);

        // End pin sits 20 µm lower in app space → -20 in Nazca (Y-up).
        line.ShouldBe("        nd.sinebend(width=0.50, distance=50.00, offset=-20.00).put(50.00, -25.00, 0.00)");
    }

    [Fact]
    public void Format_Bend_ReturnsNull_SegmentExporterOwnsIt()
    {
        // nd.bend is a (radius, angle) primitive: a single one cannot land on an arbitrary
        // end pin, so Bend exports its exact canvas segments via
        // SimpleNazcaExporter.AppendSegmentExport instead of a styled primitive line.
        var conn = CreateConnection(WaveguideType.Bend, endPinAngleDegrees: 90);
        conn.BendRadiusMicrometers = 10;

        NazcaConnectionStyleWriter.Format(conn).ShouldBeNull();
    }

    [Fact]
    public void Format_Cobra_EmitsCobraWithLocalXya()
    {
        var conn = CreateConnection(WaveguideType.Cobra, endOffsetY: 20);

        var line = NazcaConnectionStyleWriter.Format(conn);

        line.ShouldBe("        nd.cobra(xya=(50.00, -20.00, 0.00), width1=0.50, width2=0.50).put(50.00, -25.00, 0.00)");
    }

    [Fact]
    public void Format_WithGdsLayer_AppendsLayerArgument()
    {
        var conn = CreateConnection(WaveguideType.SBend, endOffsetY: 20);

        var line = NazcaConnectionStyleWriter.Format(conn, gdsLayer: 3);

        line.ShouldBe("        nd.sinebend(width=0.50, distance=50.00, offset=-20.00, layer=3).put(50.00, -25.00, 0.00)");
    }

    [Fact]
    public void Format_MissingPins_ReturnsNull()
    {
        var conn = new WaveguideConnection { Type = WaveguideType.SBend };

        NazcaConnectionStyleWriter.Format(conn).ShouldBeNull();
    }

    /// <summary>
    /// Start pin at app (50, 25) pointing 0°; end pin at app (100, 25 + endOffsetY)
    /// pointing <paramref name="endPinAngleDegrees"/>.
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
