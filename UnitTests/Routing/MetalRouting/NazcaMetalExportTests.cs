using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Components.FormulaReading;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Routing.MetalRouting;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Routing.MetalRouting;

/// <summary>
/// Tests the Nazca export of electrical connections as metal traces with
/// process-derived width/layer and bridge markers at waveguide crossings (issue #682).
/// </summary>
public class NazcaMetalExportTests
{
    [Fact]
    public void Export_Header_ContainsDefaultMetalConstants()
    {
        var canvas = new DesignCanvasViewModel();

        var result = new SimpleNazcaExporter().Export(canvas);

        result.ShouldContain("METAL_WIDTH = 10.00");
        result.ShouldContain("METAL_LAYER = (11, 0)");
        result.ShouldContain("BRIDGE_LAYER = (12, 0)");
    }

    [Fact]
    public void Export_Header_UsesProcessDerivedSpec()
    {
        var canvas = new DesignCanvasViewModel();
        var spec = new MetalRoutingSpec(8.5, 41, 2, ElectricalCrossingPolicy.DirectCrossingAllowed, 47);

        var result = new SimpleNazcaExporter().Export(canvas, metalSpec: spec);

        result.ShouldContain("METAL_WIDTH = 8.50");
        result.ShouldContain("METAL_LAYER = (41, 2)");
        result.ShouldContain("BRIDGE_LAYER = (47, 0)");
    }

    [Fact]
    public void Export_ElectricalConnection_EmitsMetalTraceNotWaveguide()
    {
        var canvas = CreateCanvasWithElectricalConnection();

        var result = new SimpleNazcaExporter().Export(canvas);

        result.ShouldContain("# Electrical Metal Traces");
        result.ShouldContain("width=METAL_WIDTH, layer=METAL_LAYER");
        result.ShouldNotContain("ic.sbend_p2p"); // electrical must not fall back to a waveguide
    }

    [Fact]
    public void Export_ElectricalConnection_DirectPolicy_EmitsNoBridges()
    {
        var canvas = CreateCanvasWithCrossingConnections();

        var result = new SimpleNazcaExporter().Export(canvas); // default: direct crossing allowed

        result.ShouldNotContain("# BRIDGE:");
    }

    [Fact]
    public void Export_ElectricalCrossesWaveguide_BridgeRequired_EmitsBridgePolygon()
    {
        var canvas = CreateCanvasWithCrossingConnections();
        var spec = MetalRoutingSpec.Default with { CrossingPolicy = ElectricalCrossingPolicy.BridgeRequired };

        var result = new SimpleNazcaExporter().Export(canvas, metalSpec: spec);

        result.ShouldContain("# BRIDGE:");
        result.ShouldContain("layer=BRIDGE_LAYER");
    }

    [Fact]
    public void Export_OpticalConnection_IsUnaffectedByMetalSpec()
    {
        var canvas = new DesignCanvasViewModel();
        var (_, _) = AddConnectedPair(canvas, electrical: false, y: 0);
        var baseline = new SimpleNazcaExporter().Export(canvas);

        var withBridges = new SimpleNazcaExporter().Export(
            canvas, metalSpec: MetalRoutingSpec.Default with { CrossingPolicy = ElectricalCrossingPolicy.BridgeRequired });

        withBridges.ShouldContain("# Waveguide Connections");
        withBridges.ShouldNotContain("# Electrical Metal Traces");
        // Optical geometry must be byte-identical regardless of the metal spec.
        StripHeader(withBridges).ShouldBe(StripHeader(baseline));
    }

    [Fact]
    public void Export_AllElectricalComponentStub_BodyOnMetalLayer()
    {
        var canvas = new DesignCanvasViewModel();
        var pad = CreateComponent("demo_pdk.probe_pad", "Pad 1", x: 0, y: 0, width: 100, height: 100);
        pad.PhysicalPins.Add(CreatePin(pad, "pad", 50, 0, 270, electrical: true));
        canvas.Components.Add(new ComponentViewModel(pad));

        var result = new SimpleNazcaExporter().Export(canvas);

        result.ShouldContain("layer=METAL_LAYER).put(0, 0)");
    }

    [Fact]
    public void Export_OpticalComponentStub_BodyStaysOnWaveguideLayer()
    {
        var canvas = new DesignCanvasViewModel();
        var comp = CreateComponent("demo_pdk.straight", "WG 1", x: 0, y: 0, width: 100, height: 10);
        comp.PhysicalPins.Add(CreatePin(comp, "a0", 0, 5, 180, electrical: false));
        canvas.Components.Add(new ComponentViewModel(comp));

        var result = new SimpleNazcaExporter().Export(canvas);

        result.ShouldNotContain("layer=METAL_LAYER).put(0, 0)");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>Removes everything before the cell body so header constants don't affect diffs.</summary>
    private static string StripHeader(string script)
    {
        var index = script.IndexOf("with nd.Cell", StringComparison.Ordinal);
        return index >= 0 ? script[index..] : script;
    }

    /// <summary>Two pads at y=45 connected by a horizontal electrical trace through y=50.</summary>
    private static DesignCanvasViewModel CreateCanvasWithElectricalConnection()
    {
        var canvas = new DesignCanvasViewModel();
        AddConnectedPair(canvas, electrical: true, y: 40);
        return canvas;
    }

    /// <summary>
    /// A horizontal electrical trace at y≈50 plus an optical connection whose route
    /// descends from y=25 to y=125 within the trace's x-range — guaranteed crossing.
    /// </summary>
    private static DesignCanvasViewModel CreateCanvasWithCrossingConnections()
    {
        var canvas = CreateCanvasWithElectricalConnection();

        var compC = CreateComponent("demo_pdk.straight", "WG C", x: 40, y: 0, width: 20, height: 20);
        var outPin = CreatePin(compC, "b0", 20, 10, 0, electrical: false);
        compC.PhysicalPins.Add(outPin);

        var compD = CreateComponent("demo_pdk.straight", "WG D", x: 120, y: 115, width: 20, height: 20);
        var inPin = CreatePin(compD, "a0", 0, 10, 180, electrical: false);
        compD.PhysicalPins.Add(inPin);

        canvas.Components.Add(new ComponentViewModel(compC));
        canvas.Components.Add(new ComponentViewModel(compD));

        var conn = new WaveguideConnection { StartPin = outPin, EndPin = inPin };
        conn.RecalculateTransmission(new WaveguideRouter());
        conn.GetPathSegments().Count.ShouldBeGreaterThan(0, "optical route must exist for the crossing test");
        canvas.Connections.Add(new WaveguideConnectionViewModel(conn));
        return canvas;
    }

    /// <summary>Adds two 10x10 components at the given y, connected left-to-right.</summary>
    private static (WaveguideConnection Connection, DesignCanvasViewModel Canvas) AddConnectedPair(
        DesignCanvasViewModel canvas, bool electrical, double y)
    {
        var compA = CreateComponent("demo_pdk.pad_a", "A", x: 0, y: y, width: 10, height: 10);
        var startPin = CreatePin(compA, "e1", 10, 5, 0, electrical);
        compA.PhysicalPins.Add(startPin);

        var compB = CreateComponent("demo_pdk.pad_b", "B", x: 200, y: y, width: 10, height: 10);
        var endPin = CreatePin(compB, "e1", 0, 5, 180, electrical);
        compB.PhysicalPins.Add(endPin);

        canvas.Components.Add(new ComponentViewModel(compA));
        canvas.Components.Add(new ComponentViewModel(compB));

        var conn = new WaveguideConnection { StartPin = startPin, EndPin = endPin };
        conn.RecalculateTransmission(new WaveguideRouter());
        canvas.Connections.Add(new WaveguideConnectionViewModel(conn));
        return (conn, canvas);
    }

    private static Component CreateComponent(
        string nazcaFunction, string identifier, double x, double y, double width, double height)
    {
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin>());
        var component = new Component(
            laserWaveLengthToSMatrixMap: new Dictionary<int, SMatrix>(),
            sliders: new List<Slider>(),
            nazcaFunctionName: nazcaFunction,
            nazcaFunctionParams: "",
            parts: parts,
            typeNumber: 0,
            identifier: identifier,
            rotationCounterClock: DiscreteRotation.R0)
        {
            WidthMicrometers = width,
            HeightMicrometers = height,
            PhysicalX = x,
            PhysicalY = y,
        };
        return component;
    }

    private static PhysicalPin CreatePin(
        Component parent, string name, double offsetX, double offsetY, double angle, bool electrical)
    {
        var matterType = electrical ? MatterType.Electricity : MatterType.Light;
        return new PhysicalPin
        {
            Name = name,
            OffsetXMicrometers = offsetX,
            OffsetYMicrometers = offsetY,
            AngleDegrees = angle,
            ParentComponent = parent,
            LogicalPin = new Pin(name, 0, matterType, RectSide.Right),
        };
    }
}
