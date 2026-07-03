using CAP.Avalonia.Services.GdsFactoryExport;
using CAP.Avalonia.Services.GdsFactoryExport.MixedBackend;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_DataAccess.Persistence.PIR;
using Shouldly;

namespace UnitTests.Export.GdsFactoryExport.MixedBackend;

/// <summary>
/// Tests for the gdsfactory host side of the mixed-backend export (issue #646):
/// Nazca-merged instances are skipped and composed in via <c>gf.import_gds</c>.
/// </summary>
public class GdsFactoryExporterMergeTests
{
    private static CAP_Core.Components.Core.Component AddComponent(
        DesignCanvasViewModel canvas, string identifier)
    {
        var component = TestComponentFactory.CreateBasicComponent();
        component.Identifier = identifier;
        component.NazcaFunctionName = "ebeam_y_1550";
        component.PhysicalX = 30;
        component.PhysicalY = 20;
        canvas.AddComponent(component, identifier);
        return component;
    }

    private static string ExportWithMerge(
        DesignCanvasViewModel canvas,
        IReadOnlyDictionary<string, NazcaCodeOverride>? overrides,
        params string[] mergedIds) =>
        new GdsFactoryExporter().Export(
            canvas, new GdsFactoryExportOptions(GdsFactoryComponentMode.UbcPdkCells), overrides,
            new NazcaGdsMerge(mergedIds.ToHashSet(StringComparer.Ordinal), "design_nazca_part.gds"));

    [Fact]
    public void Export_WithMerge_SkipsMergedInstanceAndImportsPartGds()
    {
        var canvas = new DesignCanvasViewModel();
        AddComponent(canvas, "NazcaOvr");
        var overrides = new Dictionary<string, NazcaCodeOverride>
        {
            ["NazcaOvr"] = new() { RawCode = "component = my_cell", Backend = OverrideBackend.Nazca },
        };

        var script = ExportWithMerge(canvas, overrides, "NazcaOvr");

        script.ShouldNotContain("# NazcaOvr");                        // no ubcpdk/stub placement
        script.ShouldContain("gf.import_gds(_nazca_part_path)");
        script.ShouldContain("'design_nazca_part.gds'");
        script.ShouldContain("c.add_ref(_nazca_part)");
    }

    [Fact]
    public void Export_WithMerge_KeepsGdsFactoryOverridesAndPlainInstances()
    {
        var canvas = new DesignCanvasViewModel();
        AddComponent(canvas, "NazcaOvr");
        AddComponent(canvas, "GfOvr");
        AddComponent(canvas, "Plain");
        var overrides = new Dictionary<string, NazcaCodeOverride>
        {
            ["NazcaOvr"] = new() { RawCode = "component = my_cell", Backend = OverrideBackend.Nazca },
            ["GfOvr"] = new() { RawCode = "component = gf.components.mmi1x2()", Backend = OverrideBackend.GdsFactory },
        };

        var script = ExportWithMerge(canvas, overrides, "NazcaOvr");

        script.ShouldContain("def override_GfOvr(");                  // gf override still emitted
        script.ShouldContain("c.add_ref(override_GfOvr())");
        script.ShouldContain("gf.get_component('ebeam_y_1550')");     // plain instance still placed
        script.ShouldContain("gf.import_gds(");                       // merged part imported once
    }

    [Fact]
    public void Export_WithoutMerge_HasNoImportGds()
    {
        var canvas = new DesignCanvasViewModel();
        AddComponent(canvas, "Plain");

        var script = new GdsFactoryExporter().Export(
            canvas, new GdsFactoryExportOptions(GdsFactoryComponentMode.UbcPdkCells));

        script.ShouldNotContain("gf.import_gds(");
    }
}
