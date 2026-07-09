using CAP.Avalonia.Services;
using CAP.Avalonia.Services.GdsFactoryExport;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Export;
using CAP_Core.Tiles;
using Shouldly;

namespace UnitTests.Export.MetalRouting;

/// <summary>
/// Electrical connections must export as metal traces on the metal layer — never as optical
/// waveguides (issue #682). Covers both layout emitters (Nazca and gdsfactory).
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

    private static DesignCanvasViewModel CanvasWithConnection(MatterType pinKind)
    {
        var canvas = new DesignCanvasViewModel();
        var a = MakeComponentWithPin("A", 0, "p_a", pinKind);
        var b = MakeComponentWithPin("B", 200, "p_b", pinKind);
        canvas.Components.Add(new ComponentViewModel(a));
        canvas.Components.Add(new ComponentViewModel(b));
        canvas.Connections.Add(new WaveguideConnectionViewModel(new WaveguideConnection
        {
            StartPin = a.PhysicalPins.First(p => p.Name == "p_a"),
            EndPin = b.PhysicalPins.First(p => p.Name == "p_b"),
        }));
        return canvas;
    }

    [Fact]
    public void NazcaExport_ElectricalConnection_EmitsMetalStraightNotWaveguideInterconnect()
    {
        var canvas = CanvasWithConnection(MatterType.Electricity);

        var script = new SimpleNazcaExporter().Export(canvas, metalStyle: MetalTraceStyle.Default);

        // Metal trace: a straight on the metal layer with the metal width.
        script.ShouldContain("layer=(11, 0)");
        script.ShouldContain("width=2.00");
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
    public void GdsFactoryExport_ElectricalConnection_EmitsMetalLayer()
    {
        var canvas = CanvasWithConnection(MatterType.Electricity);

        var script = new GdsFactoryExporter().Export(
            canvas, new GdsFactoryExportOptions(GdsFactoryComponentMode.StandaloneStubs),
            metalStyle: MetalTraceStyle.Default);

        script.ShouldContain("layer=(11, 0)");
        script.ShouldContain("width=2.00");
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
}
