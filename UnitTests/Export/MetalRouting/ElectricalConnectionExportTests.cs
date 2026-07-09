using CAP.Avalonia.Services;
using CAP.Avalonia.Services.GdsFactoryExport;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Export;
using CAP_Core.Routing;
using CAP_Core.Tiles;
using Shouldly;

namespace UnitTests.Export.MetalRouting;

/// <summary>
/// Electrical connections must export as metal traces on the metal layer — never as optical
/// waveguides (issue #682). Covers both layout emitters (Nazca and gdsfactory), frozen-group
/// paths, and the both-pins-electrical classification (issue #686 review, Findings 1/4/5).
/// </summary>
public class ElectricalConnectionExportTests
{
    private static Component MakeComponentWithPin(
        string id, double x, string pinName, MatterType kind)
    {
        var comp = TestComponentFactory.CreateBasicComponent();
        comp.Identifier = id;
        comp.NazcaFunctionName = "demo_pdk.heater";
        comp.PhysicalX = x;
        comp.PhysicalY = 0;
        comp.WidthMicrometers = 50;
        comp.HeightMicrometers = 30;
        comp.PhysicalPins.Add(new PhysicalPin
        {
            Name = pinName,
            ParentComponent = comp,
            OffsetXMicrometers = 25,
            OffsetYMicrometers = 0,
            AngleDegrees = 270,
            LogicalPin = new Pin(pinName, 0, kind, RectSide.Up),
        });
        return comp;
    }

    private static DesignCanvasViewModel CanvasWithConnection(MatterType startKind, MatterType? endKind = null)
    {
        var canvas = new DesignCanvasViewModel();
        var a = MakeComponentWithPin("A", 0, "p_a", startKind);
        var b = MakeComponentWithPin("B", 200, "p_b", endKind ?? startKind);
        canvas.Components.Add(new ComponentViewModel(a));
        canvas.Components.Add(new ComponentViewModel(b));
        canvas.Connections.Add(new WaveguideConnectionViewModel(new WaveguideConnection
        {
            StartPin = a.PhysicalPins.First(p => p.Name == "p_a"),
            EndPin = b.PhysicalPins.First(p => p.Name == "p_b"),
        }));
        return canvas;
    }

    /// <summary>
    /// A ComponentGroup with one frozen internal path between two pins of the given kind — used
    /// to cover the frozen-group export path separately from the live-connection loop (Finding 1).
    /// </summary>
    private static DesignCanvasViewModel CanvasWithFrozenGroupPath(MatterType pinKind)
    {
        var group = new ComponentGroup("MetalGroup");
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(0, 0, 200, 0, 0));

        var startPin = new PhysicalPin
        {
            Name = "gp_a",
            ParentComponent = group,
            OffsetXMicrometers = 0,
            OffsetYMicrometers = 0,
            LogicalPin = new Pin("gp_a", 0, pinKind, RectSide.Right),
        };
        var endPin = new PhysicalPin
        {
            Name = "gp_b",
            ParentComponent = group,
            OffsetXMicrometers = 200,
            OffsetYMicrometers = 0,
            LogicalPin = new Pin("gp_b", 0, pinKind, RectSide.Right),
        };
        group.InternalPaths.Add(new FrozenWaveguidePath { Path = path, StartPin = startPin, EndPin = endPin });

        var canvas = new DesignCanvasViewModel();
        canvas.Components.Add(new ComponentViewModel(group));
        return canvas;
    }

    [Fact]
    public void NazcaExport_ElectricalConnection_EmitsMetalStraightNotWaveguideInterconnect()
    {
        var canvas = CanvasWithConnection(MatterType.Electricity);

        var script = new SimpleNazcaExporter().Export(canvas, metalStyle: MetalTraceStyle.Default);

        // Metal trace: a straight on the metal layer with the metal width.
        script.ShouldContain("layer=(11, 0)");
        script.ShouldContain("width=10.00");
        // NOT drawn as the optical sbend interconnect that all waveguide connections use.
        script.ShouldNotContain("ic.sbend_p2p");
    }

    [Fact]
    public void NazcaExport_OpticalConnection_StaysWaveguide_NoMetalLayer()
    {
        var canvas = CanvasWithConnection(MatterType.Light);

        var script = new SimpleNazcaExporter().Export(canvas, metalStyle: MetalTraceStyle.Default);

        script.ShouldNotContain("layer=(11, 0)");
        script.ShouldContain("ic.sbend_p2p");
    }

    [Fact]
    public void NazcaExport_MixedOpticalElectricalConnection_StaysWaveguide_NotMetal()
    {
        // Finding 5: classification must require BOTH pins electrical — a mixed connection
        // (guarded at connect-time by #519, but the export predicate must not assume that)
        // must still stay an optical waveguide, not silently become a metal trace.
        var canvas = CanvasWithConnection(MatterType.Light, MatterType.Electricity);

        var script = new SimpleNazcaExporter().Export(canvas, metalStyle: MetalTraceStyle.Default);

        script.ShouldNotContain("layer=(11, 0)");
        script.ShouldContain("ic.sbend_p2p");
    }

    [Fact]
    public void NazcaExport_FrozenGroupElectricalPath_EmitsMetalLayer()
    {
        // Finding 1: a frozen electrical group path used to be exported unconditionally as an
        // optical waveguide because AppendGroupFrozenPaths never received the metal style.
        var canvas = CanvasWithFrozenGroupPath(MatterType.Electricity);

        var script = new SimpleNazcaExporter().Export(canvas, metalStyle: MetalTraceStyle.Default);

        script.ShouldContain("layer=(11, 0)");
        script.ShouldContain("width=10.00");
    }

    [Fact]
    public void NazcaExport_FrozenGroupOpticalPath_StaysWaveguide_NoMetalLayer()
    {
        var canvas = CanvasWithFrozenGroupPath(MatterType.Light);

        var script = new SimpleNazcaExporter().Export(canvas, metalStyle: MetalTraceStyle.Default);

        script.ShouldNotContain("layer=(11, 0)");
    }

    [Fact]
    public void GdsFactoryExport_ElectricalConnection_EmitsMetalPolygonOnMetalLayer_NotStraightWithLayerKwarg()
    {
        // Finding 4: gf.components.straight() has no layer= kwarg (verified TypeError against the
        // installed gdsfactory) — the metal trace must be a polygon on the metal layer instead.
        var canvas = CanvasWithConnection(MatterType.Electricity);

        var script = new GdsFactoryExporter().Export(
            canvas, new GdsFactoryExportOptions(GdsFactoryComponentMode.StandaloneStubs),
            metalStyle: MetalTraceStyle.Default);

        script.ShouldContain("c.add_polygon(");
        script.ShouldContain("layer=(11, 0)");
        script.ShouldNotMatch(@"gf\.components\.straight\([^)]*layer=");
        script.ShouldNotMatch(@"gf\.components\.bend_circular\([^)]*layer=");
    }

    [Fact]
    public void GdsFactoryExport_ElectricalConnection_PolygonSizeReflectsMetalWidth()
    {
        var canvas = CanvasWithConnection(MatterType.Electricity);
        var narrow = new MetalTraceStyle { WidthUm = 2, GdsLayer = 11, GdsDatatype = 0 };
        var wide = new MetalTraceStyle { WidthUm = 20, GdsLayer = 11, GdsDatatype = 0 };

        var narrowScript = new GdsFactoryExporter().Export(
            canvas, new GdsFactoryExportOptions(GdsFactoryComponentMode.StandaloneStubs), metalStyle: narrow);
        var wideScript = new GdsFactoryExporter().Export(
            canvas, new GdsFactoryExportOptions(GdsFactoryComponentMode.StandaloneStubs), metalStyle: wide);

        // Different trace widths must produce different polygon coordinates.
        narrowScript.ShouldNotBe(wideScript);
    }

    [Fact]
    public void GdsFactoryExport_OpticalConnection_UsesWaveguideWidth_NoMetalLayer()
    {
        var canvas = CanvasWithConnection(MatterType.Light);

        var script = new GdsFactoryExporter().Export(
            canvas, new GdsFactoryExportOptions(GdsFactoryComponentMode.StandaloneStubs),
            metalStyle: MetalTraceStyle.Default);

        script.ShouldNotContain("layer=(11, 0)");
        script.ShouldContain("width=WG_WIDTH");
    }

    [Fact]
    public void GdsFactoryExport_MixedOpticalElectricalConnection_StaysWaveguide_NotMetal()
    {
        var canvas = CanvasWithConnection(MatterType.Light, MatterType.Electricity);

        var script = new GdsFactoryExporter().Export(
            canvas, new GdsFactoryExportOptions(GdsFactoryComponentMode.StandaloneStubs),
            metalStyle: MetalTraceStyle.Default);

        script.ShouldNotContain("layer=(11, 0)");
        script.ShouldContain("width=WG_WIDTH");
    }

    [Fact]
    public void GdsFactoryExport_FrozenGroupElectricalPath_EmitsMetalPolygonOnMetalLayer()
    {
        // Finding 1 (gdsfactory side): the frozen-group loop had the same gap as Nazca's.
        var canvas = CanvasWithFrozenGroupPath(MatterType.Electricity);

        var script = new GdsFactoryExporter().Export(
            canvas, new GdsFactoryExportOptions(GdsFactoryComponentMode.StandaloneStubs),
            metalStyle: MetalTraceStyle.Default);

        script.ShouldContain("c.add_polygon(");
        script.ShouldContain("layer=(11, 0)");
    }

    [Fact]
    public void GdsFactoryExport_FrozenGroupOpticalPath_StaysWaveguide_NoMetalLayer()
    {
        var canvas = CanvasWithFrozenGroupPath(MatterType.Light);

        var script = new GdsFactoryExporter().Export(
            canvas, new GdsFactoryExportOptions(GdsFactoryComponentMode.StandaloneStubs),
            metalStyle: MetalTraceStyle.Default);

        script.ShouldNotContain("layer=(11, 0)");
    }
}
