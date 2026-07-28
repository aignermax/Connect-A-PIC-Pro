using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Routing.InterconnectRouting;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Services;

/// <summary>
/// Tests for global interconnect settings in the Nazca export header and
/// per-connection routing styles (issue #574).
/// </summary>
public class NazcaExportInterconnectSettingsTests
{
    [Fact]
    public void Export_WithoutSettingsSource_KeepsLegacyHeaderDefaults()
    {
        var exporter = new SimpleNazcaExporter();

        var result = exporter.Export(new DesignCanvasViewModel());

        result.ShouldContain("WG_WIDTH = 0.45");
        result.ShouldContain("BEND_RADIUS = 50");
        result.ShouldContain("ic = Interconnect(width=WG_WIDTH, radius=BEND_RADIUS)");
        result.ShouldNotContain("WG_LAYER");
    }

    [Fact]
    public void Export_WithCustomSettings_EmitsConfiguredHeader()
    {
        var exporter = new SimpleNazcaExporter
        {
            SettingsSource = () => new InterconnectSettings
            {
                WidthMicrometers = 0.6,
                BendRadiusMicrometers = 25,
                GdsLayer = 3,
            },
        };

        var result = exporter.Export(new DesignCanvasViewModel());

        result.ShouldContain("WG_WIDTH = 0.6");
        result.ShouldContain("BEND_RADIUS = 25");
        result.ShouldContain("WG_LAYER = 3");
        result.ShouldContain("ic = Interconnect(width=WG_WIDTH, radius=BEND_RADIUS, layer=WG_LAYER)");
    }

    [Fact]
    public void Export_ConnectionWithSBendStyle_EmitsSinebendInsteadOfSegments()
    {
        var canvas = CreateCanvasWithConnection(WaveguideType.SBend);

        var result = new SimpleNazcaExporter().Export(canvas);

        result.ShouldContain("nd.sinebend(");
    }

    [Fact]
    public void Export_ConnectionWithBendStyle_EmitsExactRouteSegments_NotSingleBendPrimitive()
    {
        // A single nd.bend(radius, angle) cannot land on an arbitrary end pin, so Bend
        // exports the exact canvas stub–arc–stub segments via the segment exporter:
        // the arc appears as an nd.bend segment line, never as one styled primitive line.
        var canvas = CreateCanvasWithConnection(
            WaveguideType.Bend, endOffsetY: 30, endPinAngleDegrees: 270);

        var result = new SimpleNazcaExporter().Export(canvas);

        result.ShouldNotContain("nd.euler(");
        result.ShouldContain("nd.bend(");
    }

    [Fact]
    public void Export_AutoConnection_DoesNotEmitStyledPrimitives()
    {
        var canvas = CreateCanvasWithConnection(WaveguideType.Auto);

        var result = new SimpleNazcaExporter().Export(canvas);

        result.ShouldNotContain("nd.sinebend(");
        result.ShouldNotContain("nd.euler(");
        result.ShouldNotContain("nd.cobra(");
    }

    private static DesignCanvasViewModel CreateCanvasWithConnection(
        WaveguideType type, double endOffsetY = 0, double endPinAngleDegrees = 180)
    {
        var canvas = new DesignCanvasViewModel();
        var compA = CreateTestComponent(0, 0);
        var compB = CreateTestComponent(200, endOffsetY);
        canvas.Components.Add(new ComponentViewModel(compA));
        canvas.Components.Add(new ComponentViewModel(compB));

        var conn = new WaveguideConnection
        {
            Type = type,
            StartPin = new PhysicalPin
            {
                Name = "output",
                OffsetXMicrometers = 50,
                OffsetYMicrometers = 25,
                AngleDegrees = 0,
                ParentComponent = compA,
            },
            EndPin = new PhysicalPin
            {
                Name = "input",
                OffsetXMicrometers = 0,
                OffsetYMicrometers = 25,
                AngleDegrees = endPinAngleDegrees,
                ParentComponent = compB,
            },
        };
        // A real routing pass gives the connection a non-null RoutedPath — required for
        // its geometry to be export-eligible (blocked/invalid/routeless routes are
        // skipped from export) — regardless of which style ends up rendering it.
        conn.RecalculateTransmission(new CAP_Core.Routing.WaveguideRouter());
        canvas.Connections.Add(new WaveguideConnectionViewModel(conn));
        return canvas;
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
