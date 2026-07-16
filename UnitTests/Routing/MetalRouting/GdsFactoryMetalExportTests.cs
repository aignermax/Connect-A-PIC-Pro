using CAP.Avalonia.Services.GdsFactoryExport;
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
/// Tests the gdsfactory export of electrical connections as extruded metal paths
/// and bridge polygons at waveguide crossings (issue #682).
/// </summary>
public class GdsFactoryMetalExportTests
{
    [Fact]
    public void Export_Header_ContainsDefaultMetalConstants()
    {
        var result = Export(new DesignCanvasViewModel());

        result.ShouldContain("METAL_WIDTH = 10.00");
        result.ShouldContain("METAL_LAYER = (11, 0)");
        result.ShouldContain("BRIDGE_LAYER = (12, 0)");
    }

    [Fact]
    public void Export_Header_UsesProcessDerivedSpec()
    {
        var spec = new MetalRoutingSpec(6.25, 41, 3, ElectricalCrossingPolicy.DirectCrossingAllowed, 48);

        var result = Export(new DesignCanvasViewModel(), spec);

        result.ShouldContain("METAL_WIDTH = 6.25");
        result.ShouldContain("METAL_LAYER = (41, 3)");
        result.ShouldContain("BRIDGE_LAYER = (48, 0)");
    }

    [Fact]
    public void Export_ElectricalConnection_EmitsExtrudedMetalPath()
    {
        var canvas = new DesignCanvasViewModel();
        AddConnectedPair(canvas, electrical: true, y: 40);

        var result = Export(canvas);

        // Metal traces are polygons on the metal layer (gf.components.straight() has no
        // layer= kwarg — #686 review, verified against the installed gdsfactory).
        result.ShouldContain("layer=(11, 0)");
        result.ShouldNotMatch(@"gf\.components\.straight\([^)]*layer=");
    }

    [Fact]
    public void Export_ElectricalConnection_NotEmittedAsWaveguide()
    {
        var canvas = new DesignCanvasViewModel();
        AddConnectedPair(canvas, electrical: true, y: 40);
        var electricalScript = Export(canvas);

        var opticalCanvas = new DesignCanvasViewModel();
        AddConnectedPair(opticalCanvas, electrical: false, y: 40);
        var opticalScript = Export(opticalCanvas);

        // The optical script routes the connection as a waveguide component; the
        // electrical one must not reuse that waveguide emission for its trace.
        opticalScript.ShouldContain("gf.components.straight(");
        electricalScript.ShouldNotContain("gf.components.straight(");
    }

    [Fact]
    public void Export_ElectricalCrossesWaveguide_BridgeRequired_EmitsBridgePolygon()
    {
        var canvas = CreateCanvasWithCrossingConnections();
        var spec = MetalRoutingSpec.Default with { CrossingPolicy = ElectricalCrossingPolicy.BridgeRequired };

        var result = Export(canvas, spec);

        result.ShouldContain("# BRIDGE:");
        result.ShouldContain("layer=BRIDGE_LAYER");
    }

    [Fact]
    public void Export_ElectricalCrossesWaveguide_DirectPolicy_EmitsNoBridges()
    {
        var canvas = CreateCanvasWithCrossingConnections();

        var result = Export(canvas); // default: direct crossing allowed

        result.ShouldNotContain("# BRIDGE:");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static string Export(DesignCanvasViewModel canvas, MetalRoutingSpec? spec = null) =>
        new GdsFactoryExporter().Export(
            canvas, new GdsFactoryExportOptions(GdsFactoryComponentMode.StandaloneStubs),
            overrides: null, metalSpec: spec);

    private static DesignCanvasViewModel CreateCanvasWithCrossingConnections()
    {
        var canvas = new DesignCanvasViewModel();
        AddConnectedPair(canvas, electrical: true, y: 40);

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

    private static void AddConnectedPair(DesignCanvasViewModel canvas, bool electrical, double y)
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
    }

    private static Component CreateComponent(
        string nazcaFunction, string identifier, double x, double y, double width, double height)
    {
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin>());
        return new Component(
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
