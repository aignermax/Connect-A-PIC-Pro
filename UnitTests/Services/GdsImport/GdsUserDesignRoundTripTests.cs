using System.Collections.ObjectModel;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components.Core;
using CAP_Core.Export;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using CAP_DataAccess.Import.Gds;
using Shouldly;
using UnitTests.Export;
using Xunit;

namespace UnitTests.Services.GdsImport;

/// <summary>
/// End-to-end round trip of a REAL user design (the one behind the "components are
/// missing after re-import" report): seven components from two bundled PDKs —
/// 2× <c>demo.mmi2x2_dp</c> ("2x2 MMI Coupler", Demo PDK) and <c>ebeam_adiabatic_te1550</c>,
/// <c>ebeam_bdc_te1550</c>, 2× <c>ebeam_crossing4</c>, <c>ebeam_dc_halfring_straight</c>
/// (SiEPIC EBeam PDK) — placed at his exact canvas coordinates, wired with his ten
/// waveguide connections, exported to GDS via <see cref="SimpleNazcaExporter"/>, the
/// script RUN with real nazca, and the produced GDS re-imported through the same
/// service path the GDS-import button uses (<see cref="GdsImportService"/> +
/// <see cref="GdsPlacementExecutor"/>, wired exactly like
/// <see cref="GdsImportDialogViewModelTests"/>).
/// <para>
/// Design-build mapping: the user's netlist pin names match the bundled templates
/// verbatim (<c>in1/in2/out1/out2</c> on the MMI, <c>port 1..4</c> on every ebeam
/// cell), so no substitution was needed. The halfring's settings
/// (<c>gap=100E-9,radius=3E-6</c>) are exactly the PDK defaults, so the plain
/// template instance already carries them — no slider fiddling. His 8 external
/// ports are NOT modeled: they are a simulation concept, and the Nazca export only
/// writes top-cell port labels for grating/edge couplers — this design has none, so
/// external ports leave no trace in the GDS either way.
/// </para>
/// <para>
/// One environment fork is pinned honestly: when the Python that runs the script
/// also has klayout + siepic_ebeam_pdk (the Lunima managed env, CI), the export's
/// klayout post-pass swaps the four ebeam stub boxes for the REAL foundry cells,
/// whose (1, 10) pin labels carry the SiEPIC names (<c>opt1..opt4</c>,
/// <c>pin1..pin4</c>). With a bare nazca-only Python the stub boxes survive and
/// their (1, 10) labels carry the app template names (<c>port 1..4</c>) — plus
/// <c>heur_N</c> edge pins, because the stub box IS waveguide-layer geometry
/// spanning the cell bounding box. Both shapes are verified and asserted per
/// scenario; everything else is identical.
/// </para>
/// </summary>
[Trait("Category", "Slow")]
public class GdsUserDesignRoundTripTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lunima-gds-userdesign-" + Guid.NewGuid().ToString("N"));
    private readonly string _prefsPath =
        Path.Combine(Path.GetTempPath(), $"lunima-gds-userdesign-prefs-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        if (File.Exists(_prefsPath)) File.Delete(_prefsPath);
    }

    [SkippableFact]
    public async Task RoundTrip_UserDesign_ExportThenReimport_ExplodesAllSevenComponents()
    {
        var python = await FindNazcaPythonAsync();
        Skip.If(python == null, "No Python with nazca available — the round trip needs the real engine.");

        // ── 1. Build the user's design verbatim from the bundled PDK templates ──
        var canvas = BuildUserDesignCanvas();
        canvas.Components.Count.ShouldBe(7);
        canvas.Connections.Count.ShouldBe(10);
        foreach (var connVm in canvas.Connections)
        {
            connVm.Connection.GetPathSegments().Count.ShouldBeGreaterThan(0,
                "every connection of the user's design is a drawn waveguide route");
        }

        // ── 2. Export with the app's own exporter; nothing may be skipped ──
        var skippedConnections = new List<string>();
        var exportWarnings = new List<string>();
        var script = new SimpleNazcaExporter().Export(
            canvas, skippedConnections: skippedConnections, exportWarnings: exportWarnings);
        skippedConnections.ShouldBeEmpty("all 10 routes are real, exportable geometry");
        exportWarnings.ShouldBeEmpty();

        // The design cell is the GDS top cell (not nazca's default wrapper), all
        // seven components are placed, and the halfring carries the user's exact
        // parameters in its stub cell name + call.
        script.ShouldContain("nd.export_gds(topcells=[design], filename=gds_filename)");
        CountLines(script, ".put('org',").ShouldBe(7, "the seven canvas components are placed");
        script.ShouldContain("demo.mmi2x2_dp().put('org', 259.70, 483.63, 0)");
        script.ShouldContain("ebeam_dc_halfring_straight_7357fa(gap=100E-9,radius=3E-6)" +
                             ".put('org', 278.19, 441.97, 0)");
        // The klayout upgrade block targets the four ebeam cells (falls back to the
        // stub boxes with a stderr warning when klayout/siepic_ebeam_pdk is absent).
        script.ShouldContain("'ebeam_dc_halfring_straight_7357fa': " +
                             "('ebeam_dc_halfring_straight', 'gap=100E-9,radius=3E-6')");

        var exportDir = Path.Combine(_root, "export");
        Directory.CreateDirectory(exportDir);
        var scriptPath = Path.Combine(exportDir, "user_design.py");
        await File.WriteAllTextAsync(scriptPath, script);
        var run = await SiepicRealGeometryExportTests.RunPythonAsync(python, exportDir, scriptPath);
        run.ExitCode.ShouldBe(0, $"nazca export script failed:\n{run.StdOut}\n{run.StdErr}");
        var gdsPath = Path.ChangeExtension(scriptPath, ".gds");
        File.Exists(gdsPath).ShouldBeTrue($"script did not write {gdsPath}:\n{run.StdOut}");

        // Scenario fork: did the klayout post-pass swap the ebeam stubs for real
        // foundry geometry? The block reports the swap on stdout.
        var siepicUpgraded = run.StdOut.Contains("SiEPIC cell(s) upgraded", StringComparison.Ordinal);

        // ── 3. GDS structure sanity (our own reader) ──
        GdsLibrary library;
        await using (var stream = File.OpenRead(gdsPath))
            library = await new GdsReader().ReadAsync(stream);

        var expectedCells = new List<string>
        {
            "ConnectAPIC_Design", "mmi2x2_dp",
            "ebeam_adiabatic_te1550", "ebeam_bdc_te1550",
            "ebeam_crossing4", "ebeam_dc_halfring_straight_7357fa",
        };
        if (siepicUpgraded)
            expectedCells.Add("Adiabatic3dB_TE_FullEtch"); // real adiabatic cell pulls in its sub-cell
        library.Cells.Keys.ShouldBe(expectedCells, ignoreOrder: true);
        library.TopCellCandidates.ShouldBe(new[] { "ConnectAPIC_Design" });

        // The design cell holds exactly the seven component references plus the
        // routed waveguides, flattened by nazca into one top-cell polygon per
        // emitted strt/bend segment.
        var designCell = library.Cells["ConnectAPIC_Design"];
        var references = designCell.Elements.OfType<GdsReference>().ToList();
        references.Count.ShouldBe(7);
        references.ShouldAllBe(r => !r.Reflected && r.AngleDegrees == 0 && r.Magnification == 1);
        references.Select(r => r.CellName).ShouldBe(new[]
        {
            "mmi2x2_dp", "mmi2x2_dp", "ebeam_adiabatic_te1550", "ebeam_bdc_te1550",
            "ebeam_crossing4", "ebeam_crossing4", "ebeam_dc_halfring_straight_7357fa",
        });
        var routedSegmentCount = CountLines(script, "nd.strt(") + CountLines(script, "nd.bend(");
        designCell.Elements.OfType<GdsPolygon>().Count().ShouldBe(routedSegmentCount,
            "nazca flattens every routed waveguide segment into a top-cell polygon — " +
            "the connections live ONLY in this geometry, not in any cell structure");

        // The demofab MMI carries its pin names as TEXT on demofab's bb_pin_text
        // layer (501, 1) — the a0/a1/b0/b1 names the re-import must surface.
        var mmiTexts = library.Cells["mmi2x2_dp"].Elements.OfType<GdsText>()
            .Where(t => t.Layer == 501 && t.TextType == 1).Select(t => t.Text).ToList();
        mmiTexts.ShouldBe(new[] { "a0", "a1", "b0", "b1" }, ignoreOrder: true);

        if (!siepicUpgraded)
        {
            // Stub scenario: each ebeam cell is the exporter's box stub — one
            // waveguide-layer box plus the four pin labels on (1, 10).
            foreach (var stubCell in new[]
                     {
                         "ebeam_adiabatic_te1550", "ebeam_bdc_te1550",
                         "ebeam_crossing4", "ebeam_dc_halfring_straight_7357fa",
                     })
            {
                var cell = library.Cells[stubCell];
                cell.Elements.OfType<GdsPolygon>().ShouldHaveSingleItem()
                    .Layer.ShouldBe(1, "the stub body is one waveguide-layer box");
                cell.Elements.OfType<GdsText>()
                    .Where(t => t.Layer == 1 && t.TextType == 10)
                    .Select(t => t.Text).ShouldBe(
                        new[] { "port 1", "port 2", "port 3", "port 4" }, ignoreOrder: true);
            }
        }

        // ── 4. Analyze: the dialog offers the design cell, not a nazca wrapper ──
        var analysis = await GdsImportService.AnalyzeAsync(gdsPath);
        analysis.TopCellCandidates.ShouldBe(new[] { "ConnectAPIC_Design" });
        analysis.TopCells.ShouldBe(new[] { new GdsTopCellSummary("ConnectAPIC_Design", 7) });

        // ── 5. Explode import through the button's service path ──
        var sink = new LibrarySink(_prefsPath);
        var service = new GdsImportService(Store("user-pdks"), () => sink.Templates.ToList(), sink.Register);
        var dialogOptions = new GdsHierarchyImportOptions
        {
            // The dialog's default port-layer field ("1,10;501,1").
            PinDetection = new GdsPinDetectionOptions { PortLayers = [(1, 10), (501, 1)] },
        };
        var outcome = await service.ImportAsync(
            gdsPath, analysis.TopCellCandidates[0], dialogOptions, null);

        // Five unique cells (the MMI and the crossing are placed twice) — every one
        // registered; all seven instances survive. No "was not registered" drops.
        outcome.RegisteredComponents.Select(r => r.CellDraftName).ShouldBe(new[]
        {
            "mmi2x2_dp", "ebeam_adiabatic_te1550", "ebeam_bdc_te1550",
            "ebeam_crossing4", "ebeam_dc_halfring_straight_7357fa",
        });
        outcome.Instances.Count.ShouldBe(7);
        outcome.Instances.Count(i => i.CellName == "mmi2x2_dp").ShouldBe(2);
        outcome.Instances.Count(i => i.CellName == "ebeam_crossing4").ShouldBe(2);

        // Pinned: ZERO reconstructed connections. His layout is SPACED — the
        // connections were drawn waveguide routes, which nazca flattens into
        // top-cell polygons (asserted above), so no two component pins abut and
        // the abutment matcher finds nothing. The v1 warning says exactly that.
        outcome.Connections.Count.ShouldBe(0);
        var warning = outcome.Warnings.ShouldHaveSingleItem();
        warning.ShouldContain("own geometry is not reconstructed");
        // Warnings stay user-presentable sentences — no raw exception text.
        warning.ShouldNotContain("Exception");
        warning.ShouldNotContain('\n');

        // The registered templates carry the pins found in the GDS:
        // the MMI via demofab's (501, 1) labels (a0/a1/b0/b1 — demofab's names for
        // what the app template calls in1/in2/out1/out2), the ebeam cells via
        // (1, 10) labels. Every draft also keeps outline polygons for the renderer.
        var mmiTemplate = sink.Templates.First(t => t.Name == "mmi2x2_dp");
        mmiTemplate.PinDefinitions.Select(p => p.Name).ShouldBe(new[] { "a0", "a1", "b0", "b1" });
        mmiTemplate.OutlinePolygons.ShouldNotBeNull().ShouldNotBeEmpty();

        var expectedEbeamPins = siepicUpgraded
            ? new Dictionary<string, string[]>
            {
                // Real foundry cells: the SiEPIC PDK's own port names (the crossing's
                // west port is literally 'opt', not 'opt1').
                ["ebeam_adiabatic_te1550"] = new[] { "opt1", "opt2", "opt3", "opt4" },
                ["ebeam_bdc_te1550"] = new[] { "opt1", "opt2", "opt3", "opt4" },
                ["ebeam_crossing4"] = new[] { "opt", "opt2", "opt4", "opt3" },
                ["ebeam_dc_halfring_straight_7357fa"] = new[] { "pin1", "pin2", "pin4", "pin3" },
            }
            : new Dictionary<string, string[]>
            {
                // Stub boxes: the app template names from the (1, 10) labels PLUS
                // heur_N edge pins — the stub box is waveguide-layer geometry
                // spanning the cell bbox, so its edge touches become heuristic pins
                // (touches wider than 100 µm are discarded: the adiabatic stub keeps
                // only its left/right edge pins).
                ["ebeam_adiabatic_te1550"] = new[]
                    { "port 1", "heur_1", "port 2", "port 4", "heur_2", "port 3" },
                ["ebeam_bdc_te1550"] = new[]
                    { "port 1", "heur_1", "port 2", "heur_2", "port 4", "heur_3", "port 3", "heur_4" },
                ["ebeam_crossing4"] = new[]
                    { "port 1", "heur_1", "port 3", "heur_2", "port 2", "heur_3", "port 4", "heur_4" },
                ["ebeam_dc_halfring_straight_7357fa"] = new[]
                    { "heur_1", "port 1", "port 2", "heur_2", "port 4", "heur_3", "port 3", "heur_4" },
            };
        foreach (var (cellName, expectedPins) in expectedEbeamPins)
        {
            var template = sink.Templates.First(t => t.Name == cellName);
            template.PinDefinitions.Select(p => p.Name).ShouldBe(expectedPins,
                $"pin set of the re-imported '{cellName}' ({(siepicUpgraded ? "real foundry cell" : "stub box")})");
            template.OutlinePolygons.ShouldNotBeNull().ShouldNotBeEmpty();
        }

        // ── 6. Placement: exactly 7 placements, 0 skipped, one named group ──
        var canvas2 = new DesignCanvasViewModel();
        var report = await new GdsPlacementExecutor(canvas2, null, () => sink.Templates.ToList())
            .ExecuteAsync(GdsPlacementPlan.FromOutcome(outcome));
        report.PlacedCount.ShouldBe(7);
        report.SkippedPlacements.ShouldBeEmpty();
        report.ConnectedCount.ShouldBe(0);
        report.Warnings.ShouldBeEmpty();
        report.GroupCreated.ShouldBeTrue();
        report.GroupName.ShouldBe("ConnectAPIC_Design");

        var group = canvas2.Components.ShouldHaveSingleItem().Component.ShouldBeOfType<ComponentGroup>();
        group.GroupName.ShouldBe("ConnectAPIC_Design");
        var children = group.GetAllComponentsRecursive().ToList();
        children.Count.ShouldBe(7);
        foreach (var child in children)
        {
            child.PhysicalPins.ShouldNotBeEmpty($"every placed component keeps its pins ({child.Identifier})");
            child.OutlinePolygons.ShouldNotBeNull().ShouldNotBeEmpty(
                $"every placed component keeps its GDS outline ({child.Identifier})");
        }

        // ── 7. Black-box mode on the same file: honestly registers NOTHING ──
        // The whole-design draft gets its pins only from the top cell's OWN port
        // labels plus the bbox edge heuristic. ConnectAPIC_Design has no own texts
        // (the exporter labels external ports only for grating/edge couplers — this
        // design has none, so its 8 external ports never became GDS labels), and no
        // waveguide-layer polygon ends exactly on the design's outer bbox (the
        // routed waveguides sit on a different layer, the MMI's bbox-spanning edges
        // are discarded as too wide). Zero pins → the draft is unpersistable and
        // nothing is placed. The warnings tell the user why, in plain text.
        var sink2 = new LibrarySink(Path.Combine(_root, "prefs-blackbox.json"));
        var service2 = new GdsImportService(Store("user-pdks-blackbox"), () => sink2.Templates.ToList(), sink2.Register);
        var blackBoxOutcome = await service2.ImportAsync(
            gdsPath, analysis.TopCellCandidates[0],
            dialogOptions with { Mode = GdsHierarchyImportMode.BlackBox }, null);

        blackBoxOutcome.RegisteredComponents.ShouldBeEmpty();
        blackBoxOutcome.Warnings.ShouldContain(
            w => w.Contains("was not registered: no pins detected", StringComparison.Ordinal));
        blackBoxOutcome.Warnings.ShouldContain(
            w => w.Contains("nothing was registered", StringComparison.Ordinal));
        blackBoxOutcome.Warnings.ShouldAllBe(w => !w.Contains("Exception") && !w.Contains('\n'));

        var canvas3 = new DesignCanvasViewModel();
        var blackBoxReport = await new GdsPlacementExecutor(canvas3, null, () => sink2.Templates.ToList())
            .ExecuteAsync(GdsPlacementPlan.FromOutcome(blackBoxOutcome));
        blackBoxReport.PlacedCount.ShouldBe(0);
        blackBoxReport.SkippedPlacements.ShouldHaveSingleItem()
            .ShouldContain("cannot be placed");
        blackBoxReport.GroupCreated.ShouldBeFalse();
        canvas3.Components.ShouldBeEmpty();
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private static int CountLines(string script, string marker) =>
        script.Split('\n').Count(l => l.Contains(marker, StringComparison.Ordinal));

    /// <summary>
    /// Rebuilds the user's design on a fresh canvas: the seven components at his
    /// exact coordinates, instantiated from the REAL bundled PDK templates (Demo
    /// PDK "2x2 MMI Coupler" ×2; SiEPIC "Adiabatic Coupler TE 1550", "Broadband DC
    /// TE 1550", "Crossing 4-Port" ×2, "DC Halfring-Straight"), then his ten
    /// waveguide connections, routed for real (the A* grid is initialized around
    /// the design's negative-Y extent like the app does).
    /// </summary>
    private static DesignCanvasViewModel BuildUserDesignCanvas()
    {
        var templates = TestPdkLoader.LoadAllTemplates();
        var canvas = new DesignCanvasViewModel();

        Component Place(string templateName, string pdk, double x, double y)
        {
            var template = templates.First(t => t.Name == templateName && t.PdkSource == pdk);
            var component = ComponentTemplates.CreateFromTemplate(template, x, y);
            canvas.AddComponent(component, templateName, pdk);
            return component;
        }

        const string demo = "Demo PDK";
        const string siepic = "SiEPIC EBeam PDK";
        // His coordinates, verbatim from the exported netlist.
        var mmi1 = Place("2x2 MMI Coupler", demo, 259.699, -513.629);       // _2x2_MMI_Coupler_1
        var mmi2 = Place("2x2 MMI Coupler", demo, 253.899, -397.528);       // _2x2_MMI_Coupler_2
        var adiabatic = Place("Adiabatic Coupler TE 1550", siepic, 298.420, -451.179);
        var bdc = Place("Broadband DC TE 1550", siepic, 730.431, -452.029);
        var crossing872 = Place("Crossing 4-Port", siepic, 542.084, -449.679);
        var crossing1175 = Place("Crossing 4-Port", siepic, 519.699, -449.679);
        var halfring = Place("DC Halfring-Straight", siepic, 267.425, -452.679);

        // His ten connections, verbatim. Pin names match the bundled templates
        // one-to-one (MMI: in1/in2/out1/out2; ebeam: "port N").
        PhysicalPin Pin(Component c, string name) => c.PhysicalPins.First(p => p.Name == name);
        canvas.ConnectPins(Pin(mmi2, "in2"), Pin(mmi1, "in1"));
        canvas.ConnectPins(Pin(mmi1, "in2"), Pin(mmi2, "in1"));
        canvas.ConnectPins(Pin(mmi1, "out2"), Pin(crossing872, "port 4"));
        canvas.ConnectPins(Pin(crossing872, "port 3"), Pin(mmi2, "out1"));
        canvas.ConnectPins(Pin(mmi2, "out2"), Pin(crossing1175, "port 3"));
        canvas.ConnectPins(Pin(crossing1175, "port 4"), Pin(mmi1, "out1"));
        canvas.ConnectPins(Pin(crossing1175, "port 2"), Pin(crossing872, "port 1"));
        canvas.ConnectPins(Pin(crossing872, "port 2"), Pin(bdc, "port 1"));
        canvas.ConnectPins(Pin(adiabatic, "port 4"), Pin(crossing1175, "port 1"));
        canvas.ConnectPins(Pin(halfring, "port 3"), Pin(adiabatic, "port 2"));

        // The app always routes on an initialized grid; the default bounds
        // (-100..5100) would not cover this design's negative-Y extent.
        canvas.InitializeAStarRouting(150, -700, 950, -250);
        canvas.RecalculateRoutesAsync().GetAwaiter().GetResult();
        return canvas;
    }

    private UserPdkStore Store(string name) => new(
        Path.Combine(_root, name), new PdkJsonSaver(), new PdkLoader());

    /// <summary>Wires the real registrar with throwaway library state (pattern from GdsImportServiceTests).</summary>
    private sealed class LibrarySink
    {
        public readonly ObservableCollection<ComponentTemplate> Templates = new();
        public readonly ObservableCollection<string> Categories = new();
        public readonly PdkManagerViewModel PdkManager = new();
        public readonly List<PdkDraft> LoadedDrafts = new();
        public readonly UserPreferencesService Preferences;
        public readonly Action<PdkComponentDraft, string, string> Register;

        public LibrarySink(string prefsPath)
        {
            Preferences = new UserPreferencesService(prefsPath);
            var loader = new PdkLoader();
            Register = (draft, pdkName, filePath) =>
                CustomComponentLibraryRegistrar.Register(
                    draft, pdkName, filePath, Templates, Categories, PdkManager,
                    Preferences, loader, LoadedDrafts, () => { }, () => { });
        }
    }

    /// <summary>
    /// Locates a Python with nazca importable: first a Lunima managed env
    /// (%LOCALAPPDATA%/Lunima/envs/*), then python/python3 on PATH (mirrors
    /// <see cref="GdsExportFullCircleTests"/>).
    /// </summary>
    private static async Task<string?> FindNazcaPythonAsync()
    {
        var envs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lunima", "envs");
        if (Directory.Exists(envs))
        {
            foreach (var root in Directory.GetDirectories(envs))
            {
                foreach (var rel in new[] { Path.Combine("Scripts", "python.exe"), Path.Combine("bin", "python") })
                {
                    var py = Path.Combine(root, rel);
                    if (File.Exists(py) && await ProbeNazca(py))
                        return py;
                }
            }
        }

        foreach (var candidate in new[] { "python", "python3" })
        {
            if (await ProbeNazca(candidate))
                return candidate;
        }
        return null;
    }

    private static async Task<bool> ProbeNazca(string python)
    {
        try
        {
            var probe = await SiepicRealGeometryExportTests.RunPythonAsync(
                python, Path.GetTempPath(), "-c", "import nazca");
            return probe.ExitCode == 0;
        }
        catch
        {
            return false;   // not on PATH at all
        }
    }
}
