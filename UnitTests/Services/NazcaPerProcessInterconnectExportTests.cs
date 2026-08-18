using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Core;
using CAP_Core.Routing;
using Shouldly;
using Xunit;

namespace UnitTests.Services;

/// <summary>
/// Per-process interconnects in the Nazca export: on a multi-process canvas (at least
/// two distinct stamped width/layer stacks) connections route through an interconnect
/// of their own cross-section (width/radius/layer) with per-segment kwargs, while
/// unstamped AND single-process designs keep the single global legacy interconnect
/// byte-identically — established GDS round-trip geometry must not change.
/// </summary>
public class NazcaPerProcessInterconnectExportTests
{
    [Fact]
    public void Export_TwoProcessDesign_EmitsOneInterconnectPerProcessCrossSection()
    {
        var canvas = new DesignCanvasViewModel();
        var csA = AddStampedComponent(canvas, "CS_A", 0, 0, width: 1.2, layer: 203);
        var csB = AddStampedComponent(canvas, "CS_B", 200, 0, width: 1.2, layer: 203);
        var siA = AddStampedComponent(canvas, "SI_A", 0, 500, width: 0.5, layer: 1);
        var siB = AddStampedComponent(canvas, "SI_B", 200, 500, width: 0.5, layer: 1);
        ConnectStraight(canvas, csA, csB);
        ConnectStraight(canvas, siA, siB);

        var script = new SimpleNazcaExporter().Export(canvas);

        // The legacy global interconnect stays the fallback for unstamped connections.
        script.ShouldContain("ic = Interconnect(width=WG_WIDTH, radius=BEND_RADIUS)");
        script.ShouldContain("ic_p1 = Interconnect(width=0.5, radius=10, layer=1)");
        script.ShouldContain("ic_p2 = Interconnect(width=1.2, radius=10, layer=203)");
        script.ShouldContain("nd.strt(length=150.00, width=1.2, layer=203).put(50.00, -25.00, 0.00)");
        script.ShouldContain("nd.strt(length=150.00, width=0.5, layer=1).put(50.00, -525.00, 0.00)");
    }

    [Fact]
    public void Export_UnstampedDesign_KeepsSingleGlobalInterconnectOnly()
    {
        var canvas = new DesignCanvasViewModel();
        var a = AddStampedComponent(canvas, "A", 0, 0, width: null, layer: null);
        var b = AddStampedComponent(canvas, "B", 200, 0, width: null, layer: null);
        ConnectStraight(canvas, a, b);

        var script = new SimpleNazcaExporter().Export(canvas);

        script.ShouldContain("ic = Interconnect(width=WG_WIDTH, radius=BEND_RADIUS)");
        script.ShouldNotContain("ic_p1");
        script.ShouldContain("nd.strt(length=150.00).put(50.00, -25.00, 0.00)");
    }

    [Fact]
    public void Export_SingleStampedProcessDesign_StaysByteIdenticalToLegacyExport()
    {
        // One stamped process stack (plus unstamped demo pins) is NOT multi-process:
        // there is nothing to distinguish, so the export must stay byte-identical to
        // the legacy global-interconnect output — the established GDS round-trip
        // geometry of demo/SiEPIC designs depends on it (PR #960 review).
        var canvas = new DesignCanvasViewModel();
        var si = AddStampedComponent(canvas, "SI_A", 0, 0, width: 0.5, layer: 1);
        var si2 = AddStampedComponent(canvas, "SI_B", 200, 0, width: 0.5, layer: 1);
        var demo = AddStampedComponent(canvas, "DEMO", 0, 500, width: null, layer: null);
        var demo2 = AddStampedComponent(canvas, "DEMO2", 200, 500, width: null, layer: null);
        ConnectStraight(canvas, si, si2);
        ConnectStraight(canvas, demo, demo2);

        var script = new SimpleNazcaExporter().Export(canvas);

        script.ShouldContain("ic = Interconnect(width=WG_WIDTH, radius=BEND_RADIUS)");
        script.ShouldNotContain("ic_p1");
        script.ShouldNotContain("width=0.5,");
        script.ShouldContain("nd.strt(length=150.00).put(50.00, -25.00, 0.00)");
        script.ShouldContain("nd.strt(length=150.00).put(50.00, -525.00, 0.00)");
    }

    [Fact]
    public void Export_RoutelessStampedConnection_FallsBackThroughItsOwnProcessInterconnect()
    {
        var canvas = new DesignCanvasViewModel();
        var a = AddStampedComponent(canvas, "A", 0, 0, width: 1.2, layer: 203);
        var b = AddStampedComponent(canvas, "B", 200, 0, width: 1.2, layer: 203);
        var siA = AddStampedComponent(canvas, "SI_A", 0, 500, width: 0.5, layer: 1);
        var siB = AddStampedComponent(canvas, "SI_B", 200, 500, width: 0.5, layer: 1);
        ConnectStraight(canvas, siA, siB);
        canvas.Connections.Add(new WaveguideConnectionViewModel(
            new CAP_Core.Components.Connections.WaveguideConnection
            {
                StartPin = a.PhysicalPins[0],
                EndPin = b.PhysicalPins[1],
            }));

        var script = new SimpleNazcaExporter().Export(canvas);

        script.ShouldContain("ic_p2 = Interconnect(width=1.2, radius=10, layer=203)");
        script.ShouldContain("ic_p2.sbend_p2p(");
        script.ShouldNotContain(" ic.sbend_p2p(");
    }

    [Fact]
    public void Export_GroupedStampedConnection_FrozenPathKeepsItsChipletCrossSection()
    {
        // Grouping freezes the connection; the frozen path keeps its endpoint pins, so the
        // chiplet's cross-section survives the freeze without any global default leaking in.
        // The second (SiEPIC-stamped) wire makes the canvas genuinely multi-process.
        var canvas = new DesignCanvasViewModel();
        var a = AddStampedComponent(canvas, "A", 0, 0, width: 1.2, layer: 203);
        var b = AddStampedComponent(canvas, "B", 200, 0, width: 1.2, layer: 203);
        var siA = AddStampedComponent(canvas, "SI_A", 0, 500, width: 0.5, layer: 1);
        var siB = AddStampedComponent(canvas, "SI_B", 200, 500, width: 0.5, layer: 1);
        ConnectStraight(canvas, siA, siB);
        ConnectStraight(canvas, a, b);
        canvas.Connections.First(c => c.Connection.StartPin == a.PhysicalPins[0])
            .Connection.IsRouteFrozen = true;
        var groupCommand = new CAP.Avalonia.Commands.CreateGroupCommand(
            canvas,
            canvas.Components.Where(vm => vm.Component == a || vm.Component == b).ToList(),
            "Chiplet");
        groupCommand.Execute();

        var script = new SimpleNazcaExporter().Export(canvas);

        script.ShouldContain("ic_p2 = Interconnect(width=1.2, radius=10, layer=203)");
        script.ShouldContain("nd.strt(length=150.00, width=1.2, layer=203)");
    }

    private static Component AddStampedComponent(
        DesignCanvasViewModel canvas, string id, double x, double y, double? width, int? layer)
    {
        var component = TestComponentFactory.CreateBasicComponent();
        component.Identifier = id;
        component.PhysicalX = x;
        component.PhysicalY = y;
        component.RotationDegrees = 0;
        component.PhysicalPins.Clear();
        component.PhysicalPins.Add(new PhysicalPin
        {
            Name = "out",
            ParentComponent = component,
            OffsetXMicrometers = 50,
            OffsetYMicrometers = 25,
            AngleDegrees = 0,
            WaveguideWidthMicrometers = width,
            Layer = layer,
        });
        component.PhysicalPins.Add(new PhysicalPin
        {
            Name = "in",
            ParentComponent = component,
            OffsetXMicrometers = 0,
            OffsetYMicrometers = 25,
            AngleDegrees = 180,
            WaveguideWidthMicrometers = width,
            Layer = layer,
        });
        canvas.AddComponent(component, id);
        return component;
    }

    private static void ConnectStraight(DesignCanvasViewModel canvas, Component a, Component b)
    {
        var from = a.PhysicalPins[0];
        var to = b.PhysicalPins[1];
        var (x1, y1) = from.GetAbsolutePosition();
        var (x2, y2) = to.GetAbsolutePosition();
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(x1, y1, x2, y2, 0));
        canvas.ConnectPinsWithCachedRoute(from, to, path).ShouldNotBeNull();
    }
}
