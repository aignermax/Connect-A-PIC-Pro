using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.ComponentSettings.InstanceOverride;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Export;
using CAP_DataAccess.Persistence.PIR;
using Shouldly;
using System.Collections.ObjectModel;

namespace UnitTests.Integration;

/// <summary>
/// Automated replacement for the manual issue-#561 acceptance test: paste the showcase
/// Nazca code into a placed component's code editor, Run Preview (REAL nazca render),
/// Apply (pins replaced, connection re-anchored by name), then export the design to GDS
/// by executing the generated script — the step that originally failed with
/// <c>KeyError: 'in1'</c> when stale pin references leaked into the export.
///
/// Requires a nazca-capable Python (resolved via <see cref="PythonDiscoveryService"/>);
/// skips cleanly when none is available so CI without nazca still passes.
/// </summary>
public class NazcaOverrideFullFlowIntegrationTests
{
    [Fact]
    public async Task FullFlow_ShowcaseOverride_PreviewApplyReanchorAndGdsExport_Succeeds()
    {
        var (python, script) = await ResolveEnvironmentAsync();
        if (python == null || script == null) return;   // env skip (CI without nazca)

        // ── Arrange: two MMIs on the canvas, connected out1 → in ────────────────
        var library = new ObservableCollection<ComponentTemplate>(TestPdkLoader.LoadAllTemplates());
        var canvas = new DesignCanvasViewModel();
        var mmiTemplate = library.First(t => t.Name == "1x2 MMI Splitter");

        var comp1 = ComponentTemplates.CreateFromTemplate(mmiTemplate, 0, 27.5);
        comp1.Identifier = "flow_mmi_1";
        canvas.AddComponent(comp1, mmiTemplate.Name);

        var comp2 = ComponentTemplates.CreateFromTemplate(mmiTemplate, 300, 27.5);
        comp2.Identifier = "flow_mmi_2";
        canvas.AddComponent(comp2, mmiTemplate.Name);

        var comp3 = ComponentTemplates.CreateFromTemplate(mmiTemplate, 700, 27.5);
        comp3.Identifier = "flow_mmi_3";
        canvas.AddComponent(comp3, mmiTemplate.Name);

        canvas.ConnectPins(
            comp1.PhysicalPins.First(p => p.Name == "out1"),
            comp2.PhysicalPins.First(p => p.Name == "in"));

        // ── Editor VM wired exactly like MainWindow.ShowComponentSettingsDialog ──
        var store = new Dictionary<string, NazcaCodeOverride>();
        var warnings = new List<string>();
        var vm = new InstanceNazcaCodeEditorViewModel(
            componentKey: comp2.Identifier,
            storedOverrides: store,
            liveComponent: comp2,
            moduleName: comp2.NazcaModuleName,
            nazcaFunction: comp2.NazcaFunctionName ?? string.Empty,
            nazcaParameters: comp2.NazcaFunctionParameters,
            templateCode: NazcaCodeTemplateBuilder.Build(
                comp2.NazcaModuleName, comp2.NazcaFunctionName, comp2.NazcaFunctionParameters),
            previewService: new NazcaComponentPreviewService(python, script),
            onPinsChanged: _ => warnings.AddRange(canvas.OnComponentPinsChanged(comp2)));

        // Dialog-open step; must leave the editor usable even when the library
        // template has no module-mode render.
        await vm.InitializeAsync();

        // ── Act 1: paste the showcase code and Run Preview (real nazca) ─────────
        vm.Code = NazcaCodeExamples.Complex;
        await vm.RunPreviewCommand.ExecuteAsync(null);

        vm.IsValid.ShouldBeTrue($"showcase preview must render. Error: {vm.PreviewError}");
        vm.PreviewData.ShouldNotBeNull();
        if (string.IsNullOrEmpty(vm.PreviewData!.PolygonWarning))   // empty overlay is legit without gdstk/gdspy
            vm.PreviewData.Polygons.Count.ShouldBeGreaterThan(0, "a preview image needs polygons");
        var previewPinNames = vm.PreviewData.Pins.Select(p => p.Name).ToList();
        previewPinNames.ShouldContain("in", "showcase defines nd.Pin('in')");
        previewPinNames.ShouldContain("out", "showcase defines nd.Pin('out')");
        previewPinNames.Count.ShouldBe(2,
            "nazca's colocated auto-default a0/b0 phantoms must be filtered out — " +
            "they would render as overlapping pins with opposite direction strokes");

        // ── Act 2: Apply — pins replaced, connection re-anchored by name ────────
        vm.ApplyOverrideCommand.Execute(null);

        comp2.PhysicalPins.Count.ShouldBe(vm.PreviewData.Pins.Count,
            "Apply must replace the template pins with the override's pins");
        comp2.PhysicalPins.Select(p => p.Name).ShouldContain("in");
        vm.HasNoSimulationModel.ShouldBeTrue(
            "override pin set (in/out) differs from the template's — no valid S-matrix mapping");
        store[comp2.Identifier].OverridePins.ShouldNotBeNull("override pins must be persisted");

        // The out1→in connection targets a pin name that still exists — it must be
        // re-anchored onto the NEW pin object, not dropped and not left stale.
        warnings.ShouldBeEmpty("no connection should be dropped — 'in' survives by name");
        canvas.Connections.Count.ShouldBe(1, "the re-anchorable connection must survive Apply");
        comp2.PhysicalPins.ShouldContain(canvas.Connections[0].Connection.EndPin,
            "EndPin must reference the current (override) pin object — a stale reference " +
            "makes the GDS export emit a pin the override cell does not define");

        // Reverse direction: a connection FROM the override's new 'out' pin TO a regular
        // PDK component — exercises the coordinate-tuple anchor as the p2p target.
        canvas.ConnectPins(
            comp2.PhysicalPins.First(p => p.Name == "out"),
            comp3.PhysicalPins.First(p => p.Name == "in"));
        canvas.Connections.Count.ShouldBe(2);

        // ── Act 3: export to GDS by EXECUTING the generated script ──────────────
        // This is the manual step that failed with KeyError: the script wires
        // comp_N.pin['in'], which the override cell defines.
        var nazcaCode = new SimpleNazcaExporter().Export(canvas, overrides: store);

        var tempDir = Directory.CreateTempSubdirectory("lunima_fullflow_").FullName;
        try
        {
            var scriptPath = Path.Combine(tempDir, "full_flow_export.py");
            await File.WriteAllTextAsync(scriptPath, nazcaCode);

            var gdsService = new GdsExportService();
            gdsService.SetCustomPythonPath(python);
            var result = await gdsService.ExportToGdsAsync(scriptPath, generateGds: true);

            result.Success.ShouldBeTrue(
                $"GDS export must run the generated script without errors. " +
                $"Error: {result.ErrorMessage}\n--- script ---\n{nazcaCode}");
            File.Exists(result.GdsPath).ShouldBeTrue("a .gds file must be produced");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ── Environment resolution (same skip pattern as NazcaEditorPreviewIntegrationTests) ──

    /// <summary>Resolves (nazca-capable python, preview script path), or (null, null) to skip.</summary>
    private static async Task<(string? python, string? script)> ResolveEnvironmentAsync()
    {
        var python = await new PythonDiscoveryService().FindFirstNazcaPythonPathAsync();
        return (python, FindRealPreviewScript());
    }

    private static string? FindRealPreviewScript() =>
        GdsAlignmentTestSetup.FindRealPreviewScript();
}
