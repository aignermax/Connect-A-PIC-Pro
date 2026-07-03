using CAP.Avalonia.Services.GdsFactoryExport.MixedBackend;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Export;
using CAP_DataAccess.Persistence.PIR;
using Shouldly;

namespace UnitTests.Export.GdsFactoryExport.MixedBackend;

/// <summary>
/// Tests for the Nazca half of the mixed-backend export (issue #646): only
/// Nazca-backend raw-code overrides are rendered, placed at the exact same
/// coordinates the full Nazca export would use.
/// </summary>
public class NazcaPartialExporterTests
{
    private static CAP_Core.Components.Core.Component AddComponent(
        DesignCanvasViewModel canvas, string identifier, double x = 30, double y = 20)
    {
        var component = TestComponentFactory.CreateBasicComponent();
        component.Identifier = identifier;
        component.NazcaFunctionName = "ebeam_y_1550";
        component.PhysicalX = x;
        component.PhysicalY = y;
        canvas.AddComponent(component, identifier);
        return component;
    }

    private static NazcaCodeOverride NazcaOverride(string code = "component = my_cell") => new()
    {
        RawCode = code,
        Backend = OverrideBackend.Nazca,
    };

    [Fact]
    public void Export_EmitsOnlyNazcaBackendOverrideInstances()
    {
        var canvas = new DesignCanvasViewModel();
        AddComponent(canvas, "Plain");
        AddComponent(canvas, "NazcaOvr");
        AddComponent(canvas, "GfOvr");
        var overrides = new Dictionary<string, NazcaCodeOverride>
        {
            ["NazcaOvr"] = NazcaOverride(),
            ["GfOvr"] = new() { RawCode = "component = gf.components.mmi1x2()", Backend = OverrideBackend.GdsFactory },
        };

        var script = new NazcaPartialExporter().Export(canvas, overrides);

        script.ShouldContain("_ovr_NazcaOvr");
        script.ShouldNotContain("_ovr_GfOvr");            // gdsfactory code stays out of the Nazca run
        script.ShouldNotContain("gf.components.mmi1x2");
        script.ShouldNotContain("Plain");                 // regular instances belong to the host
        script.ShouldNotContain("ebeam_y_1550");          // no PDK stubs/cells in the part
        script.ShouldContain($"nd.Cell(name='{NazcaPartialExporter.PartCellName}')");
        script.ShouldContain("nd.export_gds(");
    }

    [Fact]
    public void Export_PlacementMatchesTheFullNazcaExport()
    {
        // Alignment contract: the part places an instance with the exact same put(...) the
        // single-backend Nazca export emits, so the merged GDS coincides with the reference.
        var canvas = new DesignCanvasViewModel();
        var comp = AddComponent(canvas, "C1", x: 42.5, y: 17.25);
        var ovr = NazcaOverride();
        ovr.SetOverrideGeometry(width: 12, height: 8, bboxXMin: -1.5, bboxYMax: 4.0);
        var overrides = new Dictionary<string, NazcaCodeOverride> { ["C1"] = ovr };

        var partScript = new NazcaPartialExporter().Export(canvas, overrides);
        var fullScript = new CAP.Avalonia.Services.SimpleNazcaExporter().Export(canvas, overrides: overrides);

        var placement = NazcaCoordinateMapper.GetCellPlacement(comp, (-1.5, 4.0));
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var putCall = $"_ovr_C1().put('org', {placement.X.ToString("F2", ci)}, "
                      + $"{placement.Y.ToString("F2", ci)}, {placement.RotationDegrees.ToString("F0", ci)})";

        partScript.ShouldContain(putCall);
        fullScript.ShouldContain(putCall);   // both emitters agree on the placement
    }

    [Fact]
    public void Export_WithoutBboxAnchor_FallsBackToDefaultAnchorPlacement()
    {
        var canvas = new DesignCanvasViewModel();
        AddComponent(canvas, "C1");
        var overrides = new Dictionary<string, NazcaCodeOverride> { ["C1"] = NazcaOverride() };

        var script = new NazcaPartialExporter().Export(canvas, overrides);

        script.ShouldContain("_ovr_C1().put(");
        script.ShouldNotContain("put('org'");   // legacy overrides keep the default anchor
    }

    [Fact]
    public void CollectNazcaBackendOverrideIds_FiltersBackendAndEmptyCode()
    {
        var canvas = new DesignCanvasViewModel();
        AddComponent(canvas, "A");
        AddComponent(canvas, "B");
        AddComponent(canvas, "C");
        var overrides = new Dictionary<string, NazcaCodeOverride>
        {
            ["A"] = NazcaOverride(),
            ["B"] = new() { RawCode = "component = x", Backend = OverrideBackend.GdsFactory },
            ["C"] = new() { RawCode = null, Backend = OverrideBackend.Nazca },   // parameter-only
        };

        NazcaPartialExporter.CollectNazcaBackendOverrideIds(canvas, overrides).ShouldBe(new[] { "A" });
        NazcaPartialExporter.CollectNazcaBackendOverrideIds(canvas, null).ShouldBeEmpty();
    }
}
