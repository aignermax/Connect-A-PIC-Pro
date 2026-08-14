using CAP.Avalonia.Services;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Core;
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
/// klayout post-pass swaps the four ebeam stub boxes for the REAL foundry cells —
/// re-anchored into the stub frame and with the stub's (1, 10) pin labels
/// re-emitted (#811), so the pins keep the app template names (<c>port 1..4</c>)
/// at exactly the foundry pins' anchors. With a bare nazca-only Python the stub
/// boxes survive: same (1, 10) labels, PLUS <c>heur_N</c> edge pins, because the
/// stub box IS waveguide-layer geometry spanning the cell bounding box. Both
/// shapes are verified and asserted per scenario; everything else is identical.
/// </para>
/// </summary>
[Trait("Category", "Slow")]
public class GdsUserDesignRoundTripTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lunima-gds-userdesign-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
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
        using var host = new GdsDesignScopeTestHost();
        var service = host.CreateService();
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

        // Pinned: FOUR reconstructed connections. His layout is SPACED — the
        // connections were drawn waveguide routes, which nazca flattens into
        // top-cell polygon chains (asserted above). The route-network matcher
        // merges each chain and restores the four chains that span exactly two
        // pins as real connections; the remaining six chains entangle at the two
        // crossing components into TWO junction networks (25 + 13 polygons,
        // 8 + 4 pins) — crossing/junction topology is never disentangled by
        // guessing, so those stay frozen paths with informational junction notes.
        outcome.Connections.Count.ShouldBe(4);
        outcome.Connections.ShouldAllBe(c => c.IsRouteDerived && !c.IsElectrical);
        // The two MMI↔MMI braids restore with demofab pin names either way
        // (a0 of one MMI against a1 of the other, in both directions).
        outcome.Connections.ShouldContain(c =>
            c.A.InstanceIndex == 1 && c.A.PinName == "a0" && c.B.InstanceIndex == 0 && c.B.PinName == "a1");
        outcome.Connections.ShouldContain(c =>
            c.A.InstanceIndex == 0 && c.A.PinName == "a0" && c.B.InstanceIndex == 1 && c.B.PinName == "a1");
        // The two clean ebeam chains: both scenarios name the app template pins —
        // since #811 the upgrade re-emits the stub's (1,10) labels (at exactly the
        // anchors the real SiEPIC pin texts sat) instead of leaving the foundry's
        // opt*/pin* names behind.
        outcome.Connections.ShouldContain(c => c.A.PinName == "port 2" && c.B.PinName == "port 3");
        outcome.Connections.ShouldContain(c => c.A.PinName == "port 1" && c.B.PinName == "port 2");

        // Zero WARNINGS: the top-cell geometry accounting (restored/frozen) and
        // the junction notes are informational — nothing is silently dropped.
        outcome.Warnings.ShouldBeEmpty();
        outcome.Infos.ShouldContain(i => i.Contains("junction with"));
        outcome.Infos.ShouldContain(i => i.Contains("restored as 4 real connection(s)"));
        outcome.TopCellWaveguidePolygons.Count.ShouldBe(38,
            "the junction networks ride the group as frozen, non-re-routable paths");

        // The registered templates carry the pins found in the GDS:
        // the MMI via demofab's (501, 1) labels (a0/a1/b0/b1 — demofab's names for
        // what the app template calls in1/in2/out1/out2), the ebeam cells via
        // (1, 10) labels. Every draft also keeps outline polygons for the renderer.
        var mmiTemplate = host.Templates.First(t => t.Name == "mmi2x2_dp");
        mmiTemplate.PinDefinitions.Select(p => p.Name).ShouldBe(new[] { "a0", "a1", "b0", "b1" });
        mmiTemplate.OutlinePolygons.ShouldNotBeNull().ShouldNotBeEmpty();

        var expectedEbeamPins = siepicUpgraded
            ? new Dictionary<string, string[]>
            {
                // Real foundry cells with the stub's (1,10) pin labels re-emitted
                // (#811): the app template names at the foundry pins' anchors.
                ["ebeam_adiabatic_te1550"] = new[] { "port 1", "port 2", "port 4", "port 3" },
                ["ebeam_bdc_te1550"] = new[] { "port 1", "port 2", "port 4", "port 3" },
                ["ebeam_crossing4"] = new[] { "port 1", "port 3", "port 2", "port 4" },
                ["ebeam_dc_halfring_straight_7357fa"] = new[] { "port 1", "port 2", "port 4", "port 3" },
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
            var template = host.Templates.First(t => t.Name == cellName);
            template.PinDefinitions.Select(p => p.Name).ShouldBe(expectedPins,
                $"pin set of the re-imported '{cellName}' ({(siepicUpgraded ? "real foundry cell" : "stub box")})");
            template.OutlinePolygons.ShouldNotBeNull().ShouldNotBeEmpty();
        }

        // ── 6. Placement: exactly 7 placements, 0 skipped, one named group ──
        var canvas2 = new DesignCanvasViewModel();
        var report = await new GdsPlacementExecutor(canvas2, null, () => host.Templates.ToList())
            .ExecuteAsync(GdsPlacementPlan.FromOutcome(outcome));
        report.PlacedCount.ShouldBe(7);
        report.SkippedPlacements.ShouldBeEmpty();
        report.ConnectedCount.ShouldBe(4);
        report.RouteDerivedCount.ShouldBe(4);
        report.Warnings.ShouldBeEmpty();
        report.GroupCreated.ShouldBeTrue();
        report.GroupName.ShouldBe("ConnectAPIC_Design");

        var group = canvas2.Components.ShouldHaveSingleItem().Component.ShouldBeOfType<ComponentGroup>();
        group.GroupName.ShouldBe("ConnectAPIC_Design");
        group.InternalPaths.ShouldContain(p => p.StartPin == null,
            "the junction networks' polygons ride the group as pin-less frozen paths");
        group.InternalPaths.Count(p => p.StartPin == null).ShouldBe(38);
        group.InternalPaths.Count(p => p.StartPin != null).ShouldBe(4,
            "the four restored connections are frozen into the group with their pins");
        var children = group.GetAllComponentsRecursive().ToList();
        children.Count.ShouldBe(7);
        foreach (var child in children)
        {
            child.PhysicalPins.ShouldNotBeEmpty($"every placed component keeps its pins ({child.Identifier})");
            child.OutlinePolygons.ShouldNotBeNull().ShouldNotBeEmpty(
                $"every placed component keeps its GDS outline ({child.Identifier})");
        }

        // ── 7. Black-box mode on the same file: ONE component with the full pin set ──
        // The whole-design draft's pins come from the FLATTENED hierarchy: every
        // subcell's port labels, prefixed with their instance context. This design
        // has no top-cell port labels of its own (its 8 external ports are a
        // simulation concept and were never exported as labels), but the nested
        // labels give the black box a complete, unique pin set — 28 pins here —
        // so it registers and places like any other imported component.
        using var hostBlackBox = new GdsDesignScopeTestHost();
        var service2 = hostBlackBox.CreateService();
        var blackBoxOutcome = await service2.ImportAsync(
            gdsPath, analysis.TopCellCandidates[0],
            dialogOptions with { Mode = GdsHierarchyImportMode.BlackBox }, null);

        var blackBoxComponent = blackBoxOutcome.RegisteredComponents.ShouldHaveSingleItem();
        blackBoxComponent.CellDraftName.ShouldBe("ConnectAPIC_Design");
        blackBoxOutcome.Warnings.ShouldBeEmpty();
        var blackBoxTemplate = hostBlackBox.Templates.ShouldHaveSingleItem();
        blackBoxTemplate.PinDefinitions.Length.ShouldBe(28);
        blackBoxTemplate.PinDefinitions.Select(p => p.Name).ShouldContain("mmi2x2_dp#0_a0");
        blackBoxTemplate.PinDefinitions.Select(p => p.Name).ShouldContain("mmi2x2_dp#1_b1");
        blackBoxTemplate.OutlinePolygons.ShouldNotBeNull().ShouldNotBeEmpty();

        var canvas3 = new DesignCanvasViewModel();
        var blackBoxReport = await new GdsPlacementExecutor(canvas3, null, () => hostBlackBox.Templates.ToList())
            .ExecuteAsync(GdsPlacementPlan.FromOutcome(blackBoxOutcome));
        blackBoxReport.PlacedCount.ShouldBe(1);
        blackBoxReport.SkippedPlacements.ShouldBeEmpty();
        blackBoxReport.GroupCreated.ShouldBeFalse("a single placed component forms no group");
        canvas3.Components.ShouldHaveSingleItem();
    }

    // ── Harness (shared via <see cref="GdsUserDesignFixture"/>) ───────────────

    private static int CountLines(string script, string marker) =>
        GdsUserDesignFixture.CountLines(script, marker);

    private static DesignCanvasViewModel BuildUserDesignCanvas() =>
        GdsUserDesignFixture.BuildUserDesignCanvas();

    private static Task<string?> FindNazcaPythonAsync() => GdsUserDesignFixture.FindNazcaPythonAsync();
}
