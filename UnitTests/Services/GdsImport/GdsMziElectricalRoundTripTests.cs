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
/// End-to-end round trip of the user's MZI design WITH ELECTRICAL CONNECTIONS
/// (the "das hat bei mir nicht geklappt" report): ten components from the two
/// bundled PDKs at his exact coordinates (three bond pads at 180°), his six
/// waveguide connections plus his four electrical (metal-trace) connections,
/// routed for real, exported via <see cref="SimpleNazcaExporter"/> (the "Whole
/// Layout GDS" path), run with real nazca, and re-imported through the service
/// path the GDS-import button uses — in BOTH hierarchy modes.
/// <para>
/// Environment fork (same precedent as <see cref="GdsUserDesignRoundTripTests"/>):
/// when the Python also has klayout + siepic_ebeam_pdk, the export's klayout
/// post-pass swaps the bond-pad stub boxes for the real foundry cell — since #811
/// RE-ANCHORED into the stub frame with the stub's pin labels restored, so the
/// pads land exactly on their app placements and keep their re-importable 'elec'
/// pins. The import is pin-anchored for known-resolved cells, so the real cell's
/// 15 µm-wide m_pin marker paths (bbox inflated to 115.2 µm vs the 100 µm
/// template) are benign: the pins, not the bbox, anchor the placement. The
/// forced-stub run (same script, klayout/siepic imports poisoned) pins the
/// bare-nazca behavior deterministically in every environment; the upgraded run
/// is asserted in addition when it happens.
/// </para>
/// <para>
/// Honest v1 outcome for the optical side: his A*-routed layout contains three
/// genuine waveguide CROSSINGS (splitter.out1×out2, phase_shifter.out×arm.b0,
/// combiner.out1×out2 — verified as polygon overlaps in the GDS). Each crossing
/// merges two chains into one 4-pin junction network, which v1 deliberately
/// does not disentangle: those 2+2 connections come back as frozen paths with a
/// junction info naming the pins. The four clean chains restore as real
/// connections. Metal side (stub scenario): the two detector_bar traces restore
/// as ELECTRICAL connections; the two detector_cross traces cross each other
/// (10 µm-wide metal polygons genuinely overlap) and freeze as a junction.
/// </para>
/// </summary>
[Trait("Category", "Slow")]
public class GdsMziElectricalRoundTripTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lunima-gds-mzi-elec-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    // His design, keyed by instance name for the position/rotation congruence checks.
    private static readonly (string Instance, string Cell, double X, double Y, double Rot)[] Expected =
    {
        ("mmi1x2_sh#0", "mmi1x2_sh", 689.958, -512.468, 0),
        ("Phase_Shifter#0", "Phase_Shifter", 840.962, -510.468, 0),
        ("demo.shallow.strt_100#0", "demo.shallow.strt_100", 1003.253, -556.429, 0),
        ("mmi2x2_dp#0", "mmi2x2_dp", 1403.605, -514.468, 0),
        ("Photodetector#0", "Photodetector", 1664.788, -437.302, 0),
        ("Photodetector#1", "Photodetector", 1678.265, -529.500, 0),
        ("ebeam_BondPad#0", "ebeam_BondPad", 1759.788, -335.813, 180),
        ("ebeam_BondPad#1", "ebeam_BondPad", 1673.265, -638.244, 0),
        ("ebeam_BondPad#2", "ebeam_BondPad", 1922.255, -411.724, 180),
        ("ebeam_BondPad#3", "ebeam_BondPad", 1928.522, -557.244, 180),
    };

    [SkippableFact]
    public async Task RoundTrip_MziWithElectricalConnections_ExplodeMode()
    {
        var python = await GdsUserDesignFixture.FindNazcaPythonAsync();
        Skip.If(python == null, "No Python with nazca available — the round trip needs the real engine.");

        // ── 1. Build his design verbatim; every connection must be real geometry ──
        var canvas = GdsMziElectricalFixture.BuildMziCanvas();
        canvas.Components.Count.ShouldBe(10);
        canvas.Connections.Count.ShouldBe(10);
        canvas.Connections.Count(c => c.Connection.IsElectrical).ShouldBe(4,
            "his four pad/detector connections are electrical (metal traces)");
        foreach (var connVm in canvas.Connections)
        {
            connVm.Connection.GetPathSegments().Count.ShouldBeGreaterThan(0,
                "every connection of his design is a drawn route");
        }

        // ── 2. Export with the app's own exporter; nothing may be skipped ──
        var skippedConnections = new List<string>();
        var exportWarnings = new List<string>();
        var script = new SimpleNazcaExporter().Export(
            canvas, skippedConnections: skippedConnections, exportWarnings: exportWarnings);
        skippedConnections.ShouldBeEmpty("all 10 routes are real, exportable geometry");
        exportWarnings.ShouldBeEmpty();

        // The export carries everything the re-import needs:
        // — the bond-pad stub labels its electrical pin (purely electrical components
        //   have no optical nd.Pin, so the (1,10) label is the only pin trace);
        script.ShouldContain("nd.Annotation(text='elec', layer=(1, 10)).put(50.00, 50.00)");
        // — the demofab parts with electrical pins are wrapped in pin-label cells
        //   named after their templates (so the import resolves them back);
        script.ShouldContain("with nd.Cell(name='Photodetector')");
        script.ShouldContain("nd.Annotation(text='anode', layer=(1, 10)).put(45.00, 27.50)");
        script.ShouldContain("with nd.Cell(name='Phase_Shifter')");
        script.ShouldContain("lunima_pinwrap_Photodetector().put('org', 1664.79, 409.80, 0)");
        // — the parametric straight calls its stub (the real demofab call would
        //   dissolve into the top cell at export), with the bb_body frame keeping
        //   the cell bbox on the component frame;
        script.ShouldContain("demo_shallow_strt(length=100).put('org', 1003.25, 551.43, 0)");
        script.ShouldContain("with nd.Cell(name=f'demo.shallow.strt_{length}')");
        script.ShouldContain("layer=(1003, 0)");
        // — the four electrical connections are metal traces on (11, 0).
        CountLines(script, "layer=(11, 0)").ShouldBe(31,
            "his four metal routes export one metal segment per routed path segment");
        CountLines(script, ".put('org',").ShouldBe(12,
            "the ten canvas components plus the two wrapper-internal demofab puts");

        var (stubGds, upgradedGds) = await RunExportAsync(python, script);

        // ── 3. GDS structure sanity (our own reader): the evidence for where
        // the electrical connections live in the exported layout ──
        GdsLibrary library;
        await using (var stream = File.OpenRead(stubGds))
            library = await new GdsReader().ReadAsync(stream);
        library.TopCellCandidates.ShouldBe(new[] { "ConnectAPIC_Design" });
        var designCell = library.Cells["ConnectAPIC_Design"];
        var references = designCell.Elements.OfType<GdsReference>().ToList();
        references.Count.ShouldBe(10, "all ten components survive as cell references");
        references.Count(r => r.CellName == "ebeam_BondPad" && r.AngleDegrees == 180).ShouldBe(3,
            "his three 180° bond pads");
        // Optical routes flatten to top-cell polygons on nazca's default
        // interconnect layer (1111, 0); the METAL TRACES land on (11, 0) —
        // the layer the pre-fix import never looked at.
        designCell.Elements.OfType<GdsPolygon>().Count(p => p.Layer == 11 && p.DataType == 0)
            .ShouldBe(29, "the four metal routes, flattened (nazca merges collinear runs)");
        designCell.Elements.OfType<GdsPolygon>().Count(p => p.Layer == 1111 && p.DataType == 0)
            .ShouldBe(55, "the six optical routes, flattened");
        designCell.Elements.OfType<GdsPolygon>().Count(p => p.Layer == 1 && p.DataType == 0)
            .ShouldBe(0, "nothing dissolves into the top cell anymore (the straight keeps its cell)");

        // ── 4. Explode import of the FORCED-STUB GDS: the deterministic scenario ──
        var stub = await ExplodeAsync(stubGds);
        AssertExplodeStubScenario(stub);

        // ── 5. Explode import of the upgraded GDS (when klayout+siepic ran) ──
        if (upgradedGds is not null)
        {
            var upgraded = await ExplodeAsync(upgradedGds);
            AssertExplodeUpgradedScenario(upgraded);
        }
    }

    [SkippableFact]
    public async Task RoundTrip_MziWithElectricalConnections_BlackBoxMode()
    {
        var python = await GdsUserDesignFixture.FindNazcaPythonAsync();
        Skip.If(python == null, "No Python with nazca available — the round trip needs the real engine.");

        var canvas = GdsMziElectricalFixture.BuildMziCanvas();
        var script = new SimpleNazcaExporter().Export(canvas);
        var (stubGds, upgradedGds) = await RunExportAsync(python, script);

        // Stub scenario: the black box exposes the FULL flattened pin set,
        // including the four pad labels of the stub cells.
        var stub = await BlackBoxAsync(stubGds);
        var stubTemplate = stub.Template;
        stubTemplate.PinDefinitions.Length.ShouldBe(31);
        stubTemplate.PinDefinitions.Select(p => p.Name).ShouldContain("Photodetector#0_anode");
        stubTemplate.PinDefinitions.Select(p => p.Name).ShouldContain("Photodetector#1_cathode");
        stubTemplate.PinDefinitions.Select(p => p.Name).ShouldContain("mmi1x2_sh_a0");
        stubTemplate.PinDefinitions.Select(p => p.Name).ShouldContain("Phase_Shifter_elec1");
        stubTemplate.PinDefinitions.Select(p => p.Name).ShouldContain("demo.shallow.strt_100_a0");
        stubTemplate.PinDefinitions.Select(p => p.Name).ShouldContain("ebeam_BondPad#0_elec");
        // A known position: detector_bar's anode rides the first Photodetector instance.
        var anode = stubTemplate.PinDefinitions.First(p => p.Name == "Photodetector#0_anode");
        anode.OffsetX.ShouldBe(1036.29, 0.6);
        anode.OffsetY.ShouldBe(225.20, 0.6);
        // Pin-kind inference: the black box's electrical pins — the detectors'
        // anodes/cathodes (metal-trace touch), the phase shifter's elec1/elec2
        // and the four bond-pad elec pins (electrical names) — read ELECTRICAL;
        // every other pin keeps the optical default. (Previously all 31 pins
        // were pinned Light as an honestly-documented v1 limitation: geometry
        // labels carried no signal domain. That limitation is what the
        // detector's kind inference removed.)
        // The eleventh pin, the demofab PD's own contact label 'c0' on
        // detector_cross: its anchor sits 0.3 µm from the crossing metal traces
        // that sprawl over the cell's contact end there (verified by probing
        // the flattened GDS; detector_bar's c0 is 32.5 µm from any metal and
        // stays optical). Metal at the anchor IS the layer-based evidence —
        // and physically c0 is the contact pin of demofab's pd_dp_50.
        stubTemplate.PinDefinitions
            .Where(p => p.Kind == CAP_Core.Components.Core.MatterType.Electricity)
            .Select(p => p.Name)
            .ShouldBe(new[]
            {
                "Photodetector#0_anode", "Photodetector#0_cathode",
                "Photodetector#1_anode", "Photodetector#1_cathode",
                "Phase_Shifter_elec1", "Phase_Shifter_elec2",
                "ebeam_BondPad#0_elec", "ebeam_BondPad#1_elec",
                "ebeam_BondPad#2_elec", "ebeam_BondPad#3_elec",
                "pd_dp_50_$8443#1_c0",
            }, ignoreOrder: true);
        stubTemplate.OutlinePolygons.ShouldNotBeNull().ShouldNotBeEmpty();
        stub.Outcome.Warnings.ShouldBeEmpty();
        stub.Report.PlacedCount.ShouldBe(1);

        if (upgradedGds is not null)
        {
            // Upgraded scenario: the swap restores the stub's (1,10) pin labels
            // after re-anchoring (fix #811), so the pads keep their 'elec' pins —
            // the FULL pin set, same 31 as the stub scenario (pre-fix the label
            // wipe dropped the four pad pins: 27).
            var upgraded = await BlackBoxAsync(upgradedGds);
            upgraded.Template.PinDefinitions.Length.ShouldBe(31);
            upgraded.Template.PinDefinitions.Select(p => p.Name).ShouldContain("ebeam_BondPad#0_elec");
            upgraded.Outcome.Warnings.ShouldBeEmpty();
            upgraded.Report.PlacedCount.ShouldBe(1);
        }
    }

    // ── Scenario assertions ──────────────────────────────────────────────────

    private static void AssertExplodeStubScenario(ExplodeResult r)
    {
        // All ten components placed, none skipped; the three demofab cells become
        // new drafts, the pin-wrapped demofab parts and the pads resolve back to
        // the bundled templates.
        r.Outcome.RegisteredComponents.Select(c => c.CellDraftName).ShouldBe(
            new[] { "mmi1x2_sh", "demo.shallow.strt_100", "mmi2x2_dp" }, ignoreOrder: true);
        r.Outcome.Instances.Count.ShouldBe(10);
        r.Report.PlacedCount.ShouldBe(10);
        r.Report.SkippedPlacements.ShouldBeEmpty();
        r.Report.Warnings.ShouldBeEmpty();
        r.Report.GroupCreated.ShouldBeTrue();

        AssertPositionsCongruent(r.Outcome, padSlackUm: 1.0);

        // Rotations survive: the three 180° pads, everything else unrotated.
        foreach (var (instance, _, _, _, rot) in Expected)
        {
            var actual = r.Outcome.Instances.Single(i => i.InstanceName == instance).RotationDegrees;
            (actual % 360).ShouldBe(rot, $"{instance}: rotation round-trips");
        }

        // Pin-kind correctness: the resolved components keep their template kinds —
        // the electrical pins of the placed detectors/pads are electrical.
        var detectorBar = r.Canvas.Components
            .Select(c => c.Component).OfType<ComponentGroup>().Single()
            .GetAllComponentsRecursive().First(c => c.HumanReadableName == "Photodetector");
        detectorBar.PhysicalPins.Count(p => p.MatterType == CAP_Core.Components.Core.MatterType.Electricity)
            .ShouldBe(2, "anode/cathode stay electrical on the placed detector");

        // Six connections restored: his four CLEAN optical chains…
        var optical = r.Outcome.Connections.Where(c => !c.IsElectrical).ToList();
        optical.Count.ShouldBe(4);
        optical.ShouldAllBe(c => c.IsRouteDerived);
        // …and the two detector_bar metal traces as ELECTRICAL connections.
        var electrical = r.Outcome.Connections.Where(c => c.IsElectrical).ToList();
        electrical.Count.ShouldBe(2);
        electrical.ShouldAllBe(c => c.IsRouteDerived);

        // The created connections wire the right pins (demofab cell names for the
        // drafts, template names for the resolved components):
        r.Outcome.Connections.ShouldContain(c =>
            c.A.PinName == "elec" && c.B.PinName == "anode" && c.IsElectrical);
        r.Outcome.Connections.ShouldContain(c =>
            c.A.PinName == "elec" && c.B.PinName == "cathode" && c.IsElectrical);

        // On the canvas the two electrical connections sit frozen in the import
        // group (grouping freezes live connections), pins and kinds intact.
        var group = (ComponentGroup)r.Canvas.Components.Single().Component;
        var pinned = group.InternalPaths.Where(p => p.StartPin != null).ToList();
        pinned.Count.ShouldBe(6);
        pinned.Count(p => p.StartPin!.MatterType == CAP_Core.Components.Core.MatterType.Electricity
                          && p.EndPin!.MatterType == CAP_Core.Components.Core.MatterType.Electricity)
            .ShouldBe(2, "the two restored metal connections keep both-electrical pins");
        pinned.ShouldContain(p => p.StartPin!.Name == "elec" && p.EndPin!.Name == "anode");
        pinned.ShouldContain(p => p.StartPin!.Name == "elec" && p.EndPin!.Name == "cathode");

        // His two crossings stay frozen, reported as junctions with their pins:
        // the splitter-output waveguide crossing (2 optical connections) and the
        // detector_cross metal-trace crossing (2 electrical connections).
        r.Outcome.Infos.ShouldContain(i =>
            i.Contains("junction with 4 pins") && i.Contains("'a0'") && i.Contains("'b1'")
            && i.Contains("'in'") && i.Contains("'b0'"));
        r.Outcome.Infos.ShouldContain(i =>
            i.Contains("junction with 4 pins") && i.Contains("'anode'") && i.Contains("'cathode'")
            && i.Contains("'elec'"));
        r.Outcome.TopCellWaveguidePolygons.Count.ShouldBe(24,
            "14 optical + 10 metal polygons of the two junction networks ride the group as frozen paths");
        r.Report.FrozenRoutePathCount.ShouldBe(24);

        // His ask: zero WARNINGS in the clean case (infos acceptable).
        r.Outcome.Warnings.ShouldBeEmpty(
            "the clean round trip produces no warnings — junctions/frozen paths are infos");

        // The placement-time validator honestly flags that the restored
        // connections overlap near the combiner (the source geometry genuinely
        // entangles there — the junction-frozen networks prove it): the traced
        // outline of a drawn route runs along BOTH stripe edges, so the tight
        // corridors cross-detect one pair more than the old A* detours did —
        // pinned as the known traced-geometry artifact, not an import defect.
        r.Report.ValidationWarnings.Count.ShouldBe(4);
    }

    private static void AssertExplodeUpgradedScenario(ExplodeResult r)
    {
        r.Outcome.Instances.Count.ShouldBe(10);
        r.Report.PlacedCount.ShouldBe(10);
        r.Report.SkippedPlacements.ShouldBeEmpty();

        // The demofab side is unaffected by the pad upgrade: the same four clean
        // optical chains restore.
        r.Outcome.Connections.Count(c => !c.IsElectrical).ShouldBe(4);

        // Pin-anchored placement (#811 follow-up): the resolved pads place on
        // their 'elec' pin labels, so the real cell's m_pin marker paths (bbox
        // inflated to 115.2 µm) no longer shift anything — the two detector_bar
        // metal traces restore as ROUTE-DERIVED electrical connections, exactly
        // like the stub scenario. The two detector_cross traces genuinely cross
        // each other and freeze as a junction in both scenarios.
        var electrical = r.Outcome.Connections.Where(c => c.IsElectrical).ToList();
        electrical.Count.ShouldBe(2);
        electrical.ShouldAllBe(c => c.IsRouteDerived);
        r.Outcome.TopCellWaveguidePolygons.Count.ShouldBe(24,
            "same frozen remainder as the stub scenario: 14 optical + 10 metal junction polygons");

        // With the pins anchoring the placement, the marker-path bbox inflation
        // is benign — no size-mismatch warning anymore.
        r.Outcome.Warnings.ShouldBeEmpty(
            "the pads' pin labels match the template — the inflated bbox is not a mismatch");

        // Pads place on their pins (sub-µm: the export labels carry F2 rounding
        // and the klayout re-anchor is centroid-based); demofab parts stay exact.
        AssertPositionsCongruent(r.Outcome, padSlackUm: 0.5);
    }

    /// <summary>
    /// Every instance sits at its original position modulo the import's re-framing
    /// (app-space origin = the imported layout's top-left): the per-instance offset
    /// to the original canvas position must be one constant vector. Demofab parts
    /// must match within 1 µm; the pads get the scenario slack (known-resolved
    /// cells place pin-anchored — sub-µm label rounding only; pre-fix the
    /// marker-inflated bbox placed them 7.6 µm off).
    /// </summary>
    private static void AssertPositionsCongruent(GdsImportOutcome outcome, double padSlackUm)
    {
        var first = Expected[0];
        var firstInstance = outcome.Instances.Single(i => i.InstanceName == first.Instance);
        double offsetX = firstInstance.PositionXUm - first.X;
        double offsetY = firstInstance.PositionYUm - first.Y;

        foreach (var (instance, cell, x, y, _) in Expected)
        {
            var placed = outcome.Instances.Single(i => i.InstanceName == instance);
            placed.CellName.ShouldBe(cell);
            double slack = cell == "ebeam_BondPad" ? padSlackUm : 1.0;
            (placed.PositionXUm - x - offsetX).ShouldBe(0, slack,
                $"{instance}: X congruent with the original (modulo re-framing)");
            (placed.PositionYUm - y - offsetY).ShouldBe(0, slack,
                $"{instance}: Y congruent with the original (modulo re-framing)");
        }
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private sealed record ExplodeResult(GdsImportOutcome Outcome, GdsPlacementReport Report, DesignCanvasViewModel Canvas);

    private sealed record BlackBoxResult(
        GdsImportOutcome Outcome,
        GdsPlacementReport Report,
        CAP.Avalonia.ViewModels.Library.ComponentTemplate Template);

    /// <summary>
    /// Runs the export script twice: normally (the klayout post-pass upgrades the
    /// SiEPIC stub when the PDK is present) and forced-stub (klayout/siepic
    /// imports poisoned — the upgrade block downgrades to keeping the stubs).
    /// Returns (stubGds, upgradedGds-or-null-when-the-env-has-no-klayout).
    /// </summary>
    private async Task<(string StubGds, string? UpgradedGds)> RunExportAsync(string python, string script)
    {
        var exportDir = Path.Combine(_root, "export" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(exportDir);
        var scriptPath = Path.Combine(exportDir, "mzi.py");
        await File.WriteAllTextAsync(scriptPath, script);

        var run = await SiepicRealGeometryExportTests.RunPythonAsync(python, exportDir, scriptPath);
        run.ExitCode.ShouldBe(0, $"nazca export script failed:\n{run.StdOut}\n{run.StdErr}");
        var gdsPath = Path.ChangeExtension(scriptPath, ".gds");
        File.Exists(gdsPath).ShouldBeTrue($"script did not write {gdsPath}:\n{run.StdOut}");
        bool upgraded = run.StdOut.Contains("SiEPIC cell(s) upgraded", StringComparison.Ordinal);
        string? upgradedCopy = null;
        if (upgraded)
        {
            upgradedCopy = Path.Combine(exportDir, "mzi_upgraded.gds");
            File.Copy(gdsPath, upgradedCopy, overwrite: true);
        }

        var stubRunner = Path.Combine(exportDir, "mzi_stub.py");
        await File.WriteAllTextAsync(stubRunner,
            "import sys, runpy\n" +
            "sys.modules['klayout'] = None\n" +
            "sys.modules['klayout.db'] = None\n" +
            "sys.modules['siepic_ebeam_pdk'] = None\n" +
            $"sys.argv = [r'{scriptPath}']\n" +
            $"runpy.run_path(r'{scriptPath}', run_name='__main__')\n");
        var stubRun = await SiepicRealGeometryExportTests.RunPythonAsync(python, exportDir, stubRunner);
        stubRun.ExitCode.ShouldBe(0, $"forced-stub run failed:\n{stubRun.StdOut}\n{stubRun.StdErr}");
        var stubCopy = Path.Combine(exportDir, "mzi_stub.gds");
        File.Move(gdsPath, stubCopy, overwrite: true);

        return (stubCopy, upgradedCopy);
    }

    private static async Task<ExplodeResult> ExplodeAsync(string gdsPath)
    {
        using var host = new GdsDesignScopeTestHost();
        var bundled = TestPdkLoader.LoadAllTemplates();
        // Wired like the app (GdsImportButtonViewModel): the full loaded library
        // resolves known cells; newly imported drafts join it.
        var service = host.CreateService(() => bundled.Concat(host.Templates).ToList());
        var analysis = await GdsImportService.AnalyzeAsync(gdsPath);
        analysis.TopCellCandidates.ShouldBe(new[] { "ConnectAPIC_Design" });
        var dialogOptions = new GdsHierarchyImportOptions
        {
            PinDetection = new GdsPinDetectionOptions { PortLayers = [(1, 10), (501, 1)] },
        };
        var outcome = await service.ImportAsync(gdsPath, analysis.TopCellCandidates[0], dialogOptions, null);

        var canvas2 = new DesignCanvasViewModel();
        // Frozen mode: these round-trip assertions pin the traced-geometry
        // contract (frozen path counts, the traced-outline validation artifact)
        // and must stay deterministic and router-independent.
        var report = await new GdsPlacementExecutor(canvas2, null, () => bundled.Concat(host.Templates).ToList())
            .ExecuteAsync(GdsPlacementPlan.FromOutcome(outcome), rerouteImportedConnections: false);
        return new ExplodeResult(outcome, report, canvas2);
    }

    private static async Task<BlackBoxResult> BlackBoxAsync(string gdsPath)
    {
        using var host = new GdsDesignScopeTestHost();
        var bundled = TestPdkLoader.LoadAllTemplates();
        var service = host.CreateService(() => bundled.Concat(host.Templates).ToList());
        var analysis = await GdsImportService.AnalyzeAsync(gdsPath);
        var outcome = await service.ImportAsync(
            gdsPath, analysis.TopCellCandidates[0],
            new GdsHierarchyImportOptions
            {
                Mode = GdsHierarchyImportMode.BlackBox,
                PinDetection = new GdsPinDetectionOptions { PortLayers = [(1, 10), (501, 1)] },
            }, null);

        var canvas2 = new DesignCanvasViewModel();
        // Frozen mode, for the same determinism reason as the explode path.
        var report = await new GdsPlacementExecutor(canvas2, null, () => bundled.Concat(host.Templates).ToList())
            .ExecuteAsync(GdsPlacementPlan.FromOutcome(outcome), rerouteImportedConnections: false);
        var template = host.Templates.ShouldHaveSingleItem("the black box registers exactly one component");
        return new BlackBoxResult(outcome, report, template);
    }

    private static int CountLines(string script, string marker) =>
        GdsUserDesignFixture.CountLines(script, marker);
}
