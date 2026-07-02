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
}
