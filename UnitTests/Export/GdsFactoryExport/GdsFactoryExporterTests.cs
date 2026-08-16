using CAP.Avalonia.Services.GdsFactoryExport;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Export;
using CAP_Core.Routing;
using Shouldly;

namespace UnitTests.Export.GdsFactoryExport;

/// <summary>
/// Tests for the gdsfactory script generator (#581). The emitted script mirrors the
/// Nazca exporter's coordinate contract (both targets are Y-up), so expected values
/// are derived from <see cref="NazcaCoordinateMapper"/> in the tests themselves.
/// </summary>
public class GdsFactoryExporterTests
{
    private static DesignCanvasViewModel CreateCanvasWithComponent(
        string nazcaFunction, string identifier = "C1")
    {
        var canvas = new DesignCanvasViewModel();
        var component = TestComponentFactory.CreateBasicComponent();
        component.Identifier = identifier;
        component.NazcaFunctionName = nazcaFunction;
        component.PhysicalX = 30;
        component.PhysicalY = 20;
        component.RotationDegrees = 0;
        component.PhysicalPins.Add(new CAP_Core.Components.Core.PhysicalPin
        {
            Name = "opt1",
            ParentComponent = component,
            OffsetXMicrometers = 0,
            OffsetYMicrometers = 5,
            AngleDegrees = 180,
        });
        canvas.AddComponent(component, nazcaFunction);
        return canvas;
    }

    private static string ExportStandalone(DesignCanvasViewModel canvas) =>
        new GdsFactoryExporter().Export(
            canvas, new GdsFactoryExportOptions(GdsFactoryComponentMode.StandaloneStubs));

    private static string ExportUbcPdk(DesignCanvasViewModel canvas) =>
        new GdsFactoryExporter().Export(
            canvas, new GdsFactoryExportOptions(GdsFactoryComponentMode.UbcPdkCells));

    [Fact]
    public void Export_Standalone_HasGdsFactoryHeaderWithoutPdkActivation()
    {
        var script = ExportStandalone(CreateCanvasWithComponent("ebeam_y_1550"));

        script.ShouldContain("import gdsfactory as gf");
        script.ShouldNotContain("ubcpdk");
        script.ShouldContain("gf.gpdk.PDK.activate()");   // generic PDK for stub geometry
    }

    [Fact]
    public void Export_Standalone_EmitsStubWithPolygonAndPorts()
    {
        var canvas = CreateCanvasWithComponent("ebeam_y_1550");
        var script = ExportStandalone(canvas);

        script.ShouldContain("def stub_ebeam_y_1550(");
        script.ShouldContain("add_polygon(");
        script.ShouldContain("add_port(");
        script.ShouldContain("c.add_ref(stub_ebeam_y_1550(");
    }

    [Fact]
    public void Export_PlacementUsesCoordinateMapperContract()
    {
        var canvas = CreateCanvasWithComponent("ebeam_y_1550");
        var comp = canvas.Components.Single().Component;
        var placement = NazcaCoordinateMapper.GetCellPlacement(comp, rawOverrideAnchor: null);
        var script = ExportStandalone(canvas);

        script.ShouldContain($".rotate({placement.RotationDegrees.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)}");
        script.ShouldContain($".move(({placement.X.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}, "
                             + placement.Y.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Export_FooterWritesGdsNextToScript()
    {
        var script = ExportStandalone(CreateCanvasWithComponent("ebeam_y_1550"));

        script.ShouldContain("os.path.splitext(");
        script.ShouldContain(".gds'");
        script.ShouldContain("write_gds(");
        script.ShouldContain("GDS exported to:");
    }

    [Fact]
    public void Export_AnalysisToolsAreSkipped()
    {
        var canvas = CreateCanvasWithComponent("ebeam_y_1550");
        var tool = TestComponentFactory.CreateBasicComponent();
        tool.Identifier = "PWR1";
        tool.NazcaFunctionName = CAP_Core.Components.Core.Component.AnalysisToolNazcaSentinel;
        canvas.AddComponent(tool, "PowerMeter");

        var script = ExportStandalone(canvas);

        script.ShouldNotContain("PWR1");
    }

    [Fact]
    public void Export_UbcPdkMode_UsesRealCellForMappedComponents()
    {
        var script = ExportUbcPdk(CreateCanvasWithComponent("ebeam_y_1550"));

        script.ShouldContain("from ubcpdk import PDK");
        script.ShouldContain("PDK.activate()");
        script.ShouldContain("gf.get_component('ebeam_y_1550')");
        script.ShouldNotContain("def stub_ebeam_y_1550(");
    }

    [Fact]
    public void Export_UbcPdkMode_FallsBackToStubForUnmappedComponents()
    {
        var script = ExportUbcPdk(CreateCanvasWithComponent("ebeam_dc_te1550"));

        script.ShouldContain("def stub_ebeam_dc_te1550(");
        script.ShouldNotContain("gf.get_component('ebeam_dc_te1550')");
    }

    [Fact]
    public void CollectUnmappedComponents_ListsOnlyComponentsWithoutUbcPdkCell()
    {
        var canvas = CreateCanvasWithComponent("ebeam_y_1550", "A");
        var unmappedComp = TestComponentFactory.CreateBasicComponent();
        unmappedComp.Identifier = "B";
        unmappedComp.NazcaFunctionName = "ebeam_dc_te1550";
        canvas.AddComponent(unmappedComp, "DC");

        var unmapped = GdsFactoryExporter.CollectUnmappedComponents(canvas);

        unmapped.ShouldBe(new[] { "ebeam_dc_te1550" });
    }

    [Fact]
    public void SegmentWriter_StraightSegment_EmitsAbsolutelyPlacedStraight()
    {
        // App (10,20) → (60,20) horizontal; gds space negates Y.
        var segment = new StraightSegment(10, 20, 60, 20, 0);
        var sb = new System.Text.StringBuilder();

        GdsFactorySegmentWriter.AppendSegments(sb, new PathSegment[] { segment });
        var script = sb.ToString();

        script.ShouldContain("gf.components.straight(length=50.00");
        script.ShouldContain(".move((10.00, -20.00))");
    }

    [Fact]
    public void SegmentWriter_BendSegment_EmitsBendCircularWithNegatedAngles()
    {
        var bend = new BendSegment(centerX: 0, centerY: 0, radius: 25, startAngle: 90, sweepAngle: -90)
        {
            StartPoint = (10, 20),
            EndPoint = (35, 45),
        };
        var sb = new System.Text.StringBuilder();

        GdsFactorySegmentWriter.AppendSegments(sb, new PathSegment[] { bend });
        var script = sb.ToString();

        script.ShouldContain("gf.components.bend_circular(radius=25.00, angle=90.00");
        script.ShouldContain(".rotate(-90.00)");
        script.ShouldContain(".move((10.00, -20.00))");
    }

    [Fact]
    public void Export_GdsFactoryNativeDesign_RoutesWithPdkCrossSection_NotStrip()
    {
        // A routed waveguide in a gdsfactory-native design (CornerStone SiN) must use the PDK's
        // cross-section, not the generic default. gf.components.straight(width=…) resolves the
        // 'strip' cross-section, which does not exist under the activated cspdk.sin300 PDK
        // (only xs_nc/xs_no) → every export with a connection crashed at runtime (#570 field test).
        var canvas = new DesignCanvasViewModel();
        var a = CreateSinComponent("A", "cspdk.sin300.mmi1x2", 0, 0);
        var b = CreateSinComponent("B", "cspdk.sin300.straight", 50, 0);
        canvas.AddComponent(a, "SiN A");
        canvas.AddComponent(b, "SiN B");
        canvas.Connections.Add(new WaveguideConnectionViewModel(
            new CAP_Core.Components.Connections.WaveguideConnection
            {
                StartPin = a.PhysicalPins[0],
                EndPin = b.PhysicalPins[0],
            }));

        var script = ExportStandalone(canvas);

        script.ShouldContain("gf.components.straight(length=");   // the connecting waveguide was routed
        script.ShouldContain("cross_section='xs_nc'");            // …with the PDK's cross-section
        script.ShouldNotContain("width=WG_WIDTH");                // …not the generic 'strip'-width default
    }

    private static CAP_Core.Components.Core.Component CreateSinComponent(
        string id, string gdsFactoryFunction, double x, double y)
    {
        var c = TestComponentFactory.CreateBasicComponent();
        c.Identifier = id;
        c.NazcaFunctionName = "";                                  // gdsfactory-native: no Nazca function
        c.GdsFactoryFunction = gdsFactoryFunction;
        c.GdsFactoryRoutingCrossSection = "xs_nc";
        c.PhysicalX = x;
        c.PhysicalY = y;
        c.RotationDegrees = 0;
        c.PhysicalPins.Add(new CAP_Core.Components.Core.PhysicalPin
        {
            Name = "o1",
            ParentComponent = c,
            OffsetXMicrometers = 0,
            OffsetYMicrometers = 5,
            AngleDegrees = 180,
        });
        return c;
    }

    [Fact]
    public void Export_GdsFactoryBackendComponent_CallsRealFactoryAndActivatesItsPdk()
    {
        // A gdsfactory-native PDK component (e.g. CornerStone SiN via cspdk, #570) exports by
        // calling its real factory, after importing + activating its PDK module — not a stub.
        var canvas = new DesignCanvasViewModel();
        var component = TestComponentFactory.CreateBasicComponent();
        component.Identifier = "SIN1";
        component.NazcaFunctionName = "";                        // gdsfactory-native: no Nazca function
        component.GdsFactoryFunction = "cspdk.sin300.mmi1x2";
        component.PhysicalX = 30;
        component.PhysicalY = 20;
        component.RotationDegrees = 0;
        component.PhysicalPins.Add(new CAP_Core.Components.Core.PhysicalPin
        {
            Name = "opt1",
            ParentComponent = component,
            OffsetXMicrometers = 0,
            OffsetYMicrometers = 5,
            AngleDegrees = 180,
        });
        canvas.AddComponent(component, "SiN MMI");

        var script = ExportStandalone(canvas);

        script.ShouldContain("import cspdk.sin300");
        script.ShouldContain("cspdk.sin300.PDK.activate()");
        // Cells resolve from the ACTIVE PDK registry — cspdk exposes them via
        // cspdk.sin300.cells / gf.get_component, not as module attributes, so
        // "cspdk.sin300.mmi1x2()" raises AttributeError (#661 review, verified vs cspdk 1.4.4).
        script.ShouldContain("gf.get_component('mmi1x2')");
        script.ShouldNotContain("cspdk.sin300.mmi1x2()");
        script.ShouldNotContain("def stub_");            // real factory used, no stub emitted
        script.ShouldNotContain("gf.gpdk.PDK.activate()"); // gdsfactory design activates only its own PDK (#570 review)
        // A gdsfactory-backend component is not "unmapped" — no false stub-fallback warning.
        GdsFactoryExporter.CollectUnmappedComponents(canvas).ShouldBeEmpty();
        // Single gdsfactory module + no ubcpdk cells → no backend conflict.
        GdsFactoryExporter.CollectBackendConflicts(
            canvas, new GdsFactoryExportOptions(GdsFactoryComponentMode.UbcPdkCells)).ShouldBeEmpty();
    }

    [Fact]
    public void CollectBackendConflicts_TwoGdsFactoryModules_ReportsConflict()
    {
        // Two distinct gdsfactory PDK modules on one canvas cannot both be PDK-activated
        // (activation is a global singleton) — the export header would activate the last
        // one and the first module's cells would build against the wrong PDK (#661 review).
        var canvas = new DesignCanvasViewModel();
        foreach (var (id, func) in new[] { ("A", "cspdk.sin300.mmi1x2"), ("B", "cspdk.si220.mmi1x2") })
        {
            var c = TestComponentFactory.CreateBasicComponent();
            c.Identifier = id;
            c.NazcaFunctionName = "";
            c.GdsFactoryFunction = func;
            canvas.AddComponent(c, id);
        }

        var conflicts = GdsFactoryExporter.CollectBackendConflicts(
            canvas, new GdsFactoryExportOptions(GdsFactoryComponentMode.UbcPdkCells));

        conflicts.ShouldContain("cspdk.sin300");
        conflicts.ShouldContain("cspdk.si220");
    }

    [Fact]
    public void CollectBackendConflicts_GdsFactoryPlusSiepicUbcpdkCell_ReportsConflict()
    {
        // Playground lets the user mix a gdsfactory-native PDK (CornerStone SiN) with a SiEPIC
        // (ubcpdk) component. The export activates the cspdk PDK, so `gf.get_component('ebeam_…')`
        // can't resolve the SiEPIC cell → a runtime crash. This must be detected up front (#570).
        var canvas = new DesignCanvasViewModel();
        var sin = TestComponentFactory.CreateBasicComponent();
        sin.Identifier = "SIN1";
        sin.NazcaFunctionName = "";
        sin.GdsFactoryFunction = "cspdk.sin300.mmi1x2";
        canvas.AddComponent(sin, "SiN");
        var siepic = TestComponentFactory.CreateBasicComponent();
        siepic.Identifier = "EB1";
        siepic.NazcaFunctionName = "ebeam_adiabatic_te1550";   // maps to a ubcpdk cell
        canvas.AddComponent(siepic, "Adiabatic");

        var conflicts = GdsFactoryExporter.CollectBackendConflicts(
            canvas, new GdsFactoryExportOptions(GdsFactoryComponentMode.UbcPdkCells));

        conflicts.ShouldNotBeEmpty();
        conflicts.ShouldContain(c => c.Contains("cspdk.sin300"));
    }

    [Fact]
    public void Export_MixedTwoGdsFactoryModules_ActivatesEachPdkBeforeItsPlacement()
    {
        // Field decision (round 4): a mixed-process design still exports — for inspection
        // only. Each cell must be instantiated under ITS OWN PDK (activation emitted right
        // before the placement), so no cell is silently drawn with a foreign process' layers.
        var canvas = new DesignCanvasViewModel();
        foreach (var (id, func) in new[] { ("A", "cspdk.sin300.mmi1x2"), ("B", "cspdk.si220.mmi1x2") })
        {
            var c = TestComponentFactory.CreateBasicComponent();
            c.Identifier = id;
            c.NazcaFunctionName = "";
            c.GdsFactoryFunction = func;
            canvas.AddComponent(c, id);
        }

        var script = ExportUbcPdk(canvas);

        // Loud inspection-only warning in the script header.
        script.ShouldContain("NOT manufacturable");
        // Per-placement activation: each activate line sits between "c = gf.Component" and
        // the component it guards, in canvas order.
        var designStart = script.IndexOf("c = gf.Component", StringComparison.Ordinal);
        var activateSin = script.IndexOf("cspdk.sin300.PDK.activate()", StringComparison.Ordinal);
        var placeA = script.IndexOf("# A", StringComparison.Ordinal);
        var activateSi = script.IndexOf("cspdk.si220.PDK.activate()", StringComparison.Ordinal);
        var placeB = script.IndexOf("# B", StringComparison.Ordinal);
        activateSin.ShouldBeGreaterThan(designStart);
        placeA.ShouldBeGreaterThan(activateSin);
        activateSi.ShouldBeGreaterThan(placeA);
        placeB.ShouldBeGreaterThan(activateSi);
    }

    [Fact]
    public void Export_MixedModulePlusUbcCell_ActivatesUbcpdkForItsCell()
    {
        // gdsfactory-native (cspdk) + SiEPIC (ubcpdk-mapped) on one canvas: the ubcpdk cell
        // must resolve under an activated ubcpdk, not crash under the cspdk PDK.
        var canvas = new DesignCanvasViewModel();
        var sin = TestComponentFactory.CreateBasicComponent();
        sin.Identifier = "SIN1";
        sin.NazcaFunctionName = "";
        sin.GdsFactoryFunction = "cspdk.sin300.mmi1x2";
        canvas.AddComponent(sin, "SiN");
        var siepic = TestComponentFactory.CreateBasicComponent();
        siepic.Identifier = "EB1";
        siepic.NazcaFunctionName = "ebeam_y_1550";   // maps to a ubcpdk cell
        canvas.AddComponent(siepic, "Y-Branch");

        var script = ExportUbcPdk(canvas);

        script.ShouldContain("NOT manufacturable");
        script.ShouldContain("import ubcpdk");
        var activateSin = script.IndexOf("cspdk.sin300.PDK.activate()", StringComparison.Ordinal);
        var placeSin = script.IndexOf("# SIN1", StringComparison.Ordinal);
        var activateUbc = script.IndexOf("ubcpdk.PDK.activate()", StringComparison.Ordinal);
        var placeUbc = script.IndexOf("gf.get_component('ebeam_y_1550')", StringComparison.Ordinal);
        activateSin.ShouldBeGreaterThan(0);
        placeSin.ShouldBeGreaterThan(activateSin);
        activateUbc.ShouldBeGreaterThan(placeSin);
        placeUbc.ShouldBeGreaterThan(activateUbc);
    }

    private static CAP_Core.Components.Core.Component CreateRoutableComponent(
        string id, string gdsFactoryFunction, string crossSection, double x)
    {
        var c = CreateSinComponent(id, gdsFactoryFunction, x, 0);
        c.GdsFactoryRoutingCrossSection = crossSection;
        return c;
    }

    private static void Connect(
        DesignCanvasViewModel canvas,
        CAP_Core.Components.Core.Component a,
        CAP_Core.Components.Core.Component b) =>
        canvas.Connections.Add(new WaveguideConnectionViewModel(
            new CAP_Core.Components.Connections.WaveguideConnection
            {
                StartPin = a.PhysicalPins[0],
                EndPin = b.PhysicalPins[0],
            }));

    [Fact]
    public void Export_MixedProcessRouting_IsIndependentOfCanvasInsertionOrder()
    {
        // The routed waveguide's cross-section follows the connection's endpoint pins, so
        // reordering components on the canvas can no longer flip routed waveguides to
        // another process' geometry.
        string RoutingSection(params (string Id, string Func, string Xs)[] order)
        {
            var canvas = new DesignCanvasViewModel();
            var comps = order
                .Select(o => CreateRoutableComponent(o.Id, o.Func, o.Xs, o.Id == "A" ? 0 : 50))
                .ToList();
            foreach (var c in comps)
                canvas.AddComponent(c, c.Identifier);
            var sin = comps.Single(c => c.GdsFactoryRoutingCrossSection == "xs_nc");
            var si = comps.Single(c => c.GdsFactoryRoutingCrossSection == "xs_sc");
            Connect(canvas, sin, si);
            var script = ExportUbcPdk(canvas);
            var start = script.IndexOf("# Waveguide connections", StringComparison.Ordinal);
            start.ShouldBeGreaterThan(0);
            return script.Substring(start);
        }

        var sinFirst = RoutingSection(
            ("A", "cspdk.sin300.mmi1x2", "xs_nc"), ("B", "cspdk.si220.mmi1x2", "xs_sc"));
        var siFirst = RoutingSection(
            ("B", "cspdk.si220.mmi1x2", "xs_sc"), ("A", "cspdk.sin300.mmi1x2", "xs_nc"));

        // Same connection, different canvas insertion order → same routing cross-section.
        sinFirst.ShouldContain("cross_section='xs_nc'");   // the start pin's process owns the route
        sinFirst.ShouldNotContain("cross_section='xs_sc'");
        siFirst.ShouldContain("cross_section='xs_nc'");
        siFirst.ShouldNotContain("cross_section='xs_sc'");
    }

    [Fact]
    public void Export_MixedProcessRouting_EachConnectionRoutesOnItsOwnEndpointProcess()
    {
        // Two si220 components vs one sin300: the sin300 connection is NOT absorbed into the
        // majority process — each connection routes on its own endpoints' cross-section, with
        // the matching PDK activated immediately before its segments.
        var canvas = new DesignCanvasViewModel();
        var sin = CreateRoutableComponent("A", "cspdk.sin300.mmi1x2", "xs_nc", 0);
        var si1 = CreateRoutableComponent("B", "cspdk.si220.mmi1x2", "xs_sc", 50);
        var si2 = CreateRoutableComponent("C", "cspdk.si220.straight", "xs_sc", 100);
        canvas.AddComponent(sin, "SiN");
        canvas.AddComponent(si1, "Si 1");
        canvas.AddComponent(si2, "Si 2");
        Connect(canvas, sin, si1);
        Connect(canvas, si1, si2);

        var script = ExportUbcPdk(canvas);

        var connsStart = script.IndexOf("# Waveguide connections", StringComparison.Ordinal);
        connsStart.ShouldBeGreaterThan(0);
        var sinActivate = script.IndexOf("cspdk.sin300.PDK.activate()", connsStart, StringComparison.Ordinal);
        var xsNc = script.IndexOf("cross_section='xs_nc'", connsStart, StringComparison.Ordinal);
        var siActivate = script.IndexOf("cspdk.si220.PDK.activate()", connsStart, StringComparison.Ordinal);
        var xsSc = script.IndexOf("cross_section='xs_sc'", connsStart, StringComparison.Ordinal);
        sinActivate.ShouldBeGreaterThan(0, "the sin300 connection activates its own PDK");
        xsNc.ShouldBeGreaterThan(sinActivate, "xs_nc routes under the sin300 PDK");
        siActivate.ShouldBeGreaterThan(xsNc, "the si220-internal connection switches back to its own PDK");
        xsSc.ShouldBeGreaterThan(siActivate, "xs_sc routes under the si220 PDK");
    }

    [Fact]
    public void Export_SingleBackend_HasNoMixedProcessWarning()
    {
        // Single-process designs keep the proven script shape — no warning, no
        // per-placement activation churn.
        var script = ExportUbcPdk(CreateCanvasWithComponent("ebeam_y_1550"));

        script.ShouldNotContain("NOT manufacturable");
        script.ShouldContain("from ubcpdk import PDK");   // unchanged single-backend header
    }

    [Fact]
    public void BareGdsFactoryFunction_FallsToStubAndIsReportedUnmapped()
    {
        // A dotless gdsFactoryFunction has no importable module → it must fall through to a
        // stub AND be surfaced as unmapped, not silently emit an unresolvable factory call.
        var canvas = new DesignCanvasViewModel();
        var c = TestComponentFactory.CreateBasicComponent();
        c.Identifier = "BARE";
        c.NazcaFunctionName = "mystery_cell";
        c.GdsFactoryFunction = "mmi1x2";   // no module part
        canvas.AddComponent(c, "Bare");

        var script = ExportStandalone(canvas);

        script.ShouldNotContain("gf.get_component('mmi1x2')");
        GdsFactoryExporter.CollectUnmappedComponents(canvas).ShouldContain("mystery_cell");
    }
}
