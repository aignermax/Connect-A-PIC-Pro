using CAP.Avalonia.Services.GdsFactoryExport;
using CAP.Avalonia.Services.GdsFactoryExport.MixedBackend;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.Export.GdsFactoryExport.MixedBackend;

/// <summary>
/// End-to-end proof of the mixed-backend contract: both generated scripts actually run —
/// the nazca partial first (SiEPIC cells upgraded to real foundry geometry), then the
/// gdsfactory main script, which merges the partial via <c>gf.import_gds()</c> — and the
/// resulting single GDS contains BOTH groups: the upgraded real ebeam cell and the
/// gdsfactory-side component. String-level tests cannot catch merge mechanics (top-cell
/// naming, relative paths, coordinate sharing), so this executes the real thing in a
/// managed env; skips where no such Python exists.
/// </summary>
[Trait("Category", "Slow")]
public class MixedBackendMergeIntegrationTests
{
    [SkippableFact]
    public async Task MixedBackendExport_RunBothScripts_MergedGdsContainsBothGroups()
    {
        var python = SiepicRealGeometryExportTests.FindSiepicPython();
        Skip.If(python == null,
            "No Python with klayout+siepic_ebeam_pdk+nazca+gdsfactory (expected on CI without the full env).");

        var dir = Path.Combine(Path.GetTempPath(), "mixed_merge_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var mainPath = Path.Combine(dir, "chip.py");
            var partialPath = MixedBackendGdsOrchestrator.PartialScriptPathFor(mainPath);
            var scripts = new MixedBackendGdsOrchestrator().BuildScripts(
                MixedCanvas(), new GdsFactoryExportOptions(GdsFactoryComponentMode.UbcPdkCells),
                metalSpec: null, Array.Empty<ComponentTemplate>(), mainPath);
            await File.WriteAllTextAsync(partialPath, scripts.NazcaPartialScript);
            await File.WriteAllTextAsync(mainPath, scripts.GdsFactoryScript);

            var partial = await SiepicRealGeometryExportTests.RunPythonAsync(python, dir, partialPath);
            partial.ExitCode.ShouldBe(0, $"nazca partial failed:\n{partial.StdOut}\n{partial.StdErr}");
            var main = await SiepicRealGeometryExportTests.RunPythonAsync(python, dir, mainPath);
            main.ExitCode.ShouldBe(0, $"gdsfactory main failed:\n{main.StdOut}\n{main.StdErr}");

            var gdsPath = Path.ChangeExtension(mainPath, ".gds");
            File.Exists(gdsPath).ShouldBeTrue("the main script did not write the merged GDS");

            const string inspect =
                "import sys, json, klayout.db as db\n" +
                "ly = db.Layout()\n" +
                "ly.read(sys.argv[1])\n" +
                "counts = {c.name: sum(c.shapes(li).size() for li in ly.layer_indexes()) for c in ly.each_cell()}\n" +
                "print(json.dumps(counts))";
            var probe = await SiepicRealGeometryExportTests.RunPythonAsync(python, dir, "-c", inspect, gdsPath);
            probe.ExitCode.ShouldBe(0, $"GDS inspection failed:\n{probe.StdErr}");
            var counts = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(probe.StdOut.Trim())!;

            counts.ShouldContainKey(MixedBackendGdsOrchestrator.NazcaPartialTopCellName);
            counts.ShouldContainKey("ebeam_dc_te1550");
            counts["ebeam_dc_te1550"].ShouldBeGreaterThan(1,
                "the merged GDS must carry the upgraded real SiEPIC cell, not the 1-polygon stub");
            counts.Values.Count(v => v > 0).ShouldBeGreaterThan(1,
                "geometry from both backend groups must be present in the merged GDS");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp cleanup best effort */ }
        }
    }

    /// <summary>One gdsfactory-native built-in and one SiEPIC directional coupler.</summary>
    private static DesignCanvasViewModel MixedCanvas()
    {
        var canvas = new DesignCanvasViewModel();
        var gf = TestComponentFactory.CreateBasicComponent();
        gf.Identifier = "GF1";
        gf.NazcaFunctionName = "";
        gf.GdsFactoryFunction = "mmi2x2";
        gf.WidthMicrometers = 10;
        gf.HeightMicrometers = 5;
        canvas.AddComponent(gf, "MMI");
        var dc = TestComponentFactory.CreateBasicComponent();
        dc.Identifier = "DC1";
        dc.NazcaFunctionName = "ebeam_dc_te1550";
        dc.NazcaModuleName = "siepic_ebeam_pdk";
        dc.NazcaFunctionParameters = "gap=200E-9";
        dc.WidthMicrometers = 22.02;
        dc.HeightMicrometers = 6.2;
        dc.NazcaOriginOffsetX = 11.01;
        dc.NazcaOriginOffsetY = 3.1;
        canvas.AddComponent(dc, "Directional Coupler TE 1550");
        return canvas;
    }
}
