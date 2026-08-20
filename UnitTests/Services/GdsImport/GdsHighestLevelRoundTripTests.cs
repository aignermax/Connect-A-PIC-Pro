using System.Diagnostics;
using System.Globalization;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components.Core;
using CAP_Core.Export;
using CAP_Core.Export.Netlist;
using CAP_DataAccess.Import.Gds;
using Shouldly;
using UnitTests.Export;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace UnitTests.Services.GdsImport;

/// <summary>
/// The highest-level integration test of the GDS round-trip story: the user's real
/// 7-component mixed-PDK design (<see cref="GdsUserDesignFixture"/>) is exported to GDS
/// with the app's own <see cref="SimpleNazcaExporter"/> (run with real nazca),
/// cross-checked INDEPENDENTLY from Python (klayout/gdstk), re-imported through the
/// button's service path (<see cref="GdsImportService"/> + <see cref="GdsPlacementExecutor"/>,
/// frozen imported geometry), and finally compared as a NETLIST (gdsfactory YAML, parsed
/// with YamlDotNet) against the original canvas — topology, not names.
/// <para>
/// Pinned loop numbers (SiEPIC-upgraded scenario — klayout + siepic_ebeam_pdk present,
/// i.e. the Lunima managed env and CI): the export writes 7 top-cell references over
/// 5 device cells; the re-import registers 5 drafts and places 7 instances at the
/// original coordinates (uniform origin shift, ≤1 µm slack from the real foundry
/// cells' bounding boxes); the abutment matcher reconstructs 0 connections (his 10
/// connections were routed waveguides — flattened top-cell geometry, no abutting
/// pins); the route-network matcher restores the five polygon chains that span
/// exactly two pins (the two MMI braids, halfring↔adiabatic 10.6 µm,
/// bdc↔crossing, crossing↔crossing); the remaining five connections entangle into
/// ONE junction network across the crossing components — never disentangled by
/// guessing, so their polygons ride the group as frozen, pin-less paths with an
/// informational note. The imported netlist is therefore a SUBSET of the
/// original topology: 5 of 10 edges, pin-exact, zero miswired edges. Placement
/// runs with <c>rerouteImportedConnections: false</c>: these are netlist-TOPOLOGY
/// equivalence tests, and frozen imported geometry keeps them deterministic and
/// independent of the live router.
/// </para>
/// <para>
/// Pin-name mapping used for the topology comparison (original template pin →
/// re-imported pin, verified GEOMETRICALLY against the placed pins within 1 µm):
/// demofab MMI in1/in2/out1/out2 → a0/a1/b0/b1 (its (501,1) bb_pin_text labels);
/// the ebeam cells keep their app template names "port 1..4" — since #811 the
/// klayout upgrade re-emits the stub's (1,10) pin labels (at exactly the anchors
/// the real SiEPIC pin texts sat — the calibrated app pins coincide with the
/// foundry pins to rounding) and drops the real cells' SiEPIC-named pin texts.
/// Component-class mapping for the netlist instances:
/// "demo.mmi2x2_dp" (module-qualified nazca name) and the imported drafts'
/// fabricated "nazca_&lt;cellname&gt;" (raw-code components carry no nazca
/// function — a documented convention, see GdsCellDraftMapperTests) both map to
/// the GDS cell name; the halfring's parameter-hash stub cell name
/// (ebeam_dc_halfring_straight_7357fa) maps back to ebeam_dc_halfring_straight.
/// </para>
/// <para>
/// The stub scenario (bare-nazca python: the four ebeam cells stay 1-polygon stub
/// boxes) is forced in <see cref="FullLoop_StubScenario_RouteDerivationRestoresMmiBraids_WithoutMiswires"/>
/// by stripping the klayout upgrade call from the export script — the same GDS a
/// bare-nazca environment would produce. There the two MMI braids still restore
/// ROUTE-DERIVED (their route polygons touch exactly the two labeled demofab
/// a-pins each), while the stubs' heuristic <c>heur_N</c> edge pins entangle
/// everything else into junction networks that stay frozen paths: 2/10 restored,
/// zero miswired connections — the honest v1 outcome.
/// </para>
/// </summary>
[Trait("Category", "Slow")]
public class GdsHighestLevelRoundTripTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lunima-gds-highlevel-" + Guid.NewGuid().ToString("N"));
    private readonly List<GdsDesignScopeTestHost> _hosts = new();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        foreach (var host in _hosts) host.Dispose();
    }

    // ── The full loop ────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task FullLoop_UserDesign_ExportReimport_NetlistTopologyMatchesOriginal()
    {
        // ── 1+2. Export the user's design with the app's exporter and run it ──
        var export = await ExportUserDesignAsync("export", stripSiepicUpgrade: false);
        export.SkippedConnections.ShouldBeEmpty("all 10 routes are real, exportable geometry");
        export.ExportWarnings.ShouldBeEmpty();
        GdsUserDesignFixture.CountLines(export.Script, ".put('org',").ShouldBe(7,
            "the seven canvas components are placed in the export script");

        // ── 3. Analyze + explode-import through the button's service path ──
        var (outcome, host) = await ImportExplodeAsync(export.GdsPath);
        outcome.RegisteredComponents.Select(r => r.CellDraftName).ShouldBe(new[]
        {
            "mmi2x2_dp", "ebeam_adiabatic_te1550", "ebeam_bdc_te1550",
            "ebeam_crossing4", "ebeam_dc_halfring_straight_7357fa",
        });
        outcome.Instances.Count.ShouldBe(7);

        // The honest connection outcome: his layout is SPACED — the 10 logical
        // connections are routed waveguides that nazca flattens into top-cell
        // polygon chains. The route-network matcher restores the five chains
        // that span exactly two pins (the two MMI braids, adiabatic↔halfring,
        // bdc↔crossing, crossing↔crossing); the remaining five entangle into ONE
        // junction network across the crossing components (32 polygons, 10 pins)
        // — never disentangled by guessing, so those stay frozen paths with an
        // informational note. (With the largest-viable-radius snap of the styled
        // routes, #888, the wider arcs pick different winners in the congested
        // crossing area: bdc↔crossing and crossing↔crossing restore cleanly,
        // adiabatic↔crossing entangles — one net additional clean chain. The
        // collision-checked terminal-approach arcs of #1084 re-fragment the
        // frozen network: 32 polygons, was 39 — the 5/5 restore split is unchanged.)
        outcome.Connections.Count.ShouldBe(5);
        outcome.Connections.ShouldAllBe(c => c.IsRouteDerived);
        outcome.Warnings.ShouldBeEmpty("restored/frozen accounting is informational now");
        outcome.TopCellWaveguidePolygons.Count.ShouldBe(32,
            "the junction network's polygons ride the group as frozen, non-routable paths");

        // ── 4. Place with frozen imported geometry: this is a netlist-TOPOLOGY
        // equivalence test, and frozen mode keeps it deterministic and
        // router-independent (the re-route path is covered elsewhere) ──
        var canvas2 = new DesignCanvasViewModel();
        canvas2.InitializeAStarRouting(150, -700, 950, -250);
        var report = await new GdsPlacementExecutor(canvas2, null, () => host.Templates.ToList())
            .ExecuteAsync(GdsPlacementPlan.FromOutcome(outcome), rerouteImportedConnections: false);

        report.PlacedCount.ShouldBe(7);
        report.SkippedPlacements.ShouldBeEmpty();
        report.ConnectedCount.ShouldBe(5,
            "the five clean two-pin route chains restore as real connections (route-derived)");
        report.RouteDerivedCount.ShouldBe(5);
        report.ReroutedCount.ShouldBe(0, "frozen mode hands nothing to the live router");
        report.Warnings.ShouldBeEmpty();
        report.ValidationWarnings.ShouldBeEmpty(
            "the five route chains keep their drawn polygons as frozen cached routes — no " +
            "re-route, so the old A* braid jitter (blocked/overlapping) no longer trips the validator");
        report.CachedRouteCount.ShouldBe(5,
            "all five restored chains load with their drawn geometry as hardcoded paths (issue #811)");
        report.GroupCreated.ShouldBeTrue();
        report.GroupName.ShouldBe("ConnectAPIC_Design");

        var group = canvas2.Components.ShouldHaveSingleItem().Component.ShouldBeOfType<ComponentGroup>();
        var children = group.GetAllComponentsRecursive().ToList();
        children.Count.ShouldBe(7);

        if (export.SiepicUpgraded)
        {
            group.ExternalPins.Count.ShouldBe(18,
                "28 pins minus the ten consumed by the five restored connections stay free");
            AssertPlacementsMatchOriginals(export.Canvas, children, positionToleranceUm: 1.0);
            AssertPinMappingGeometrically(export.Canvas, children);
            AssertEveryChildIsVisible(children, expectedPinsPerChild: 4);
            AssertNetlistTopologyUpgraded(export.Canvas, canvas2);
        }
        else
        {
            // Bare-nazca environment: the same outcome the forced-stub test pins.
            group.ExternalPins.Count.ShouldBe(42,
                "46 pins minus the four consumed by the two restored braids stay free");
            AssertPlacementsMatchOriginals(export.Canvas, children, positionToleranceUm: 1.0);
            AssertEveryChildIsVisible(children, expectedPinsPerChild: null);
            AssertNetlistTopologyStub(export.Canvas, canvas2);
        }

        // ── 6. Error-channel sanity across the whole loop ──
        AssertMessageChannels(
            export.SkippedConnections, export.ExportWarnings, export.StdErr,
            outcome.Warnings, outcome.Infos, report.Warnings, report.ValidationWarnings);
    }

    // ── Python cross-check of the intermediate GDS ───────────────────────────

    /// <summary>
    /// Answers "does the export make sense independently of our own reader?": a tiny
    /// Python script (written to TEMP, launched through <see cref="ProcessLaunchFactory"/>
    /// like the app's external-tool services) reads the exported GDS with klayout or
    /// gdstk and reports the structure, which this test asserts.
    /// </summary>
    [SkippableFact]
    public async Task ExportedGds_IndependentPythonCrossCheck_ConfirmsDesignStructure()
    {
        var export = await ExportUserDesignAsync("export-pycheck", stripSiepicUpgrade: false);

        var engine = await ProbeGdsEngineAsync(export.Python);
        Skip.If(engine == null, "Python has neither klayout.db nor gdstk — no independent GDS reader.");

        var checkPath = Path.Combine(_root, "gds_cross_check.py");
        await File.WriteAllTextAsync(checkPath, CrossCheckScript);
        var run = await RunViaFactoryAsync(export.Python, _root, checkPath, engine, export.GdsPath);
        run.ExitCode.ShouldBe(0, $"python cross-check failed:\n{run.StdOut}\n{run.StdErr}");
        var values = run.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);

        // One top cell — the design cell, not a nazca wrapper.
        values["TOP"].ShouldBe("ConnectAPIC_Design");
        // Exactly the seven component instances hang off the design cell …
        values["TOPREFS"].ShouldBe("7");
        // … over the five unique device cells (the demofab MMI among them).
        values["REFCELLS"].ShouldBe(
            "ebeam_adiabatic_te1550,ebeam_bdc_te1550,ebeam_crossing4," +
            "ebeam_dc_halfring_straight_7357fa,mmi2x2_dp");
        // Cell census: design cell + 5 device cells, plus the real adiabatic
        // cell's sub-cell when the klayout upgrade swapped the stubs.
        values["CELLS"].ShouldBe(export.SiepicUpgraded ? "7" : "6");
        // Pin labels: the four ebeam cells carry 4 labels each on (1,10); the
        // demofab MMI carries its a0/a1/b0/b1 names on (501,1) bb_pin_text.
        values["TEXTS_1_10"].ShouldBe("16");
        values["TEXTS_501_1"].ShouldBe("4");
    }

    /// <summary>The tiny cross-check script: prints KEY=VALUE lines for the C# side.</summary>
    private const string CrossCheckScript = """
        import sys
        engine, path = sys.argv[1], sys.argv[2]
        if engine == "klayout":
            import klayout.db as db
            ly = db.Layout()
            ly.read(path)
            print("TOP=" + ",".join(sorted(c.name for c in ly.top_cells())))
            names = sorted(c.name for c in ly.each_cell())
            print("CELLS=%d" % len(names))
            print("CELLNAMES=" + ",".join(names))
            design = ly.cell("ConnectAPIC_Design")
            refs = [inst.cell.name for inst in design.each_inst()]
            print("TOPREFS=%d" % len(refs))
            print("REFCELLS=" + ",".join(sorted(set(refs))))
            for layer, dtype in ((1, 10), (501, 1)):
                li = ly.find_layer(layer, dtype)
                n = 0
                if li is not None:
                    for c in ly.each_cell():
                        for sh in c.shapes(li).each():
                            if sh.is_text():
                                n += 1
                print("TEXTS_%d_%d=%d" % (layer, dtype, n))
        else:
            import gdstk
            lib = gdstk.read_gds(path)
            print("TOP=" + ",".join(sorted(c.name for c in lib.top_level())))
            names = sorted(c.name for c in lib.cells)
            print("CELLS=%d" % len(names))
            print("CELLNAMES=" + ",".join(names))
            design = {c.name: c for c in lib.cells}["ConnectAPIC_Design"]
            refs = [r.cell_name for r in design.references]
            print("TOPREFS=%d" % len(refs))
            print("REFCELLS=" + ",".join(sorted(set(refs))))
            for layer, dtype in ((1, 10), (501, 1)):
                n = sum(1 for c in lib.cells for lbl in c.labels
                        if lbl.layer == layer and lbl.texttype == dtype)
                print("TEXTS_%d_%d=%d" % (layer, dtype, n))
        """;

    // ── Forced stub scenario ─────────────────────────────────────────────────

    /// <summary>
    /// Forces the bare-nazca scenario on ANY machine: the export script's klayout
    /// upgrade call is stripped, so the ebeam cells stay 1-polygon stub boxes whose
    /// waveguide-layer bodies spawn heuristic <c>heur_N</c> edge pins next to the
    /// labeled ones. The heur pollution entangles every ebeam chain into junction
    /// networks that stay frozen — but the two MMI braids still come back
    /// ROUTE-DERIVED: their flattened route polygons each touch exactly the two
    /// demofab a-pins, which carry real labels (no heur pollution).
    /// </summary>
    [SkippableFact]
    public async Task FullLoop_StubScenario_RouteDerivationRestoresMmiBraids_WithoutMiswires()
    {
        var export = await ExportUserDesignAsync("export-stub", stripSiepicUpgrade: true);
        export.SiepicUpgraded.ShouldBeFalse("the upgrade call was stripped — the stubs survive");

        var (outcome, host) = await ImportExplodeAsync(export.GdsPath);
        outcome.Instances.Count.ShouldBe(7);
        outcome.Connections.ShouldAllBe(c => c.IsRouteDerived,
            "structural restoration only — nothing guessed");
        outcome.Connections.Count.ShouldBe(2,
            "the two MMI braids (a0↔a1 both directions) restore from their route polygons");
        outcome.Infos.ShouldContain(i => i.Contains("restored as 2 real connection"),
            "the geometry report moved to the info channel with the restored/frozen split");

        // Frozen mode, like the full-loop test: the netlist comparison must stay
        // deterministic and router-independent.
        var canvas2 = new DesignCanvasViewModel();
        canvas2.InitializeAStarRouting(150, -700, 950, -250);
        var report = await new GdsPlacementExecutor(canvas2, null, () => host.Templates.ToList())
            .ExecuteAsync(GdsPlacementPlan.FromOutcome(outcome), rerouteImportedConnections: false);

        report.PlacedCount.ShouldBe(7);
        report.SkippedPlacements.ShouldBeEmpty();
        report.ConnectedCount.ShouldBe(2, "the two route-derived MMI braids");
        report.ReroutedCount.ShouldBe(0, "frozen mode hands nothing to the live router");
        report.Warnings.ShouldBeEmpty();
        report.ValidationWarnings.ShouldBeEmpty(
            "the two braided cross-links keep their drawn geometry as frozen cached routes — " +
            "the same shape the original canvas shows (red-dashed detours in panel 01), loaded " +
            "verbatim instead of re-routed through the old A* degradation");
        report.CachedRouteCount.ShouldBe(2,
            "both braids load with their drawn geometry as hardcoded paths (issue #811)");
        report.GroupCreated.ShouldBeTrue();

        var group = canvas2.Components.ShouldHaveSingleItem().Component.ShouldBeOfType<ComponentGroup>();
        var children = group.GetAllComponentsRecursive().ToList();
        children.Count.ShouldBe(7);

        group.ExternalPins.Count.ShouldBe(42,
            "46 pins (28 labeled + 18 heuristic) minus the four consumed by the two restored braids stay free");
        AssertPlacementsMatchOriginals(export.Canvas, children, positionToleranceUm: 1.0);
        AssertEveryChildIsVisible(children, expectedPinsPerChild: null);
        AssertNetlistTopologyStub(export.Canvas, canvas2);
        AssertMessageChannels(
            export.SkippedConnections, export.ExportWarnings, export.StdErr,
            outcome.Warnings, outcome.Infos, report.Warnings, report.ValidationWarnings);
    }

    // ── Stage assertions ─────────────────────────────────────────────────────

    /// <summary>
    /// Every placed child sits at its original component's position modulo the
    /// uniform origin shift (the import re-origins at the layout's top-left,
    /// which includes the routed waveguides extending past the components).
    /// The shift is anchored on the exact mmi2x2_dp placement; the SiEPIC cells
    /// add ≤0.5 µm of bounding-box slack (real foundry cell vs. app template).
    /// Pairing is by component class + position rank.
    /// </summary>
    internal static void AssertPlacementsMatchOriginals(
        DesignCanvasViewModel originalCanvas,
        IReadOnlyList<Component> placedChildren,
        double positionToleranceUm)
    {
        var pairs = PairByClassAndRank(originalCanvas, placedChildren);
        var (anchorOriginal, anchorPlaced) = pairs.First(p =>
            p.original.HumanReadableName == "2x2 MMI Coupler" && p.original.PhysicalY < -500);
        var dx = anchorOriginal.PhysicalX - anchorPlaced.PhysicalX;
        var dy = anchorOriginal.PhysicalY - anchorPlaced.PhysicalY;

        foreach (var (original, placed) in pairs)
        {
            (placed.PhysicalX + dx).ShouldBe(original.PhysicalX, positionToleranceUm,
                $"X of {original.HumanReadableName} round-trips (origin shift removed)");
            (placed.PhysicalY + dy).ShouldBe(original.PhysicalY, positionToleranceUm,
                $"Y of {original.HumanReadableName} round-trips (Y-flip must round-trip)");
            placed.RotationDegrees.ShouldBe(0.0, 1e-9, "all references are unrotated, like the originals");
        }
    }

    /// <summary>
    /// Verifies the documented pin-name mapping GEOMETRICALLY: for every mapped
    /// original→imported pin pair of every class-matched component pair, the
    /// placed pin's absolute position (origin shift removed) matches the original
    /// pin's within 1 µm — the export wrote the same coordinates and the import
    /// detected the labels at those spots.
    /// </summary>
    private static void AssertPinMappingGeometrically(
        DesignCanvasViewModel originalCanvas, IReadOnlyList<Component> placedChildren)
    {
        var pairs = PairByClassAndRank(originalCanvas, placedChildren);
        var (anchorOriginal, anchorPlaced) = pairs.First(p =>
            p.original.HumanReadableName == "2x2 MMI Coupler" && p.original.PhysicalY < -500);
        var dx = anchorOriginal.PhysicalX - anchorPlaced.PhysicalX;
        var dy = anchorOriginal.PhysicalY - anchorPlaced.PhysicalY;

        foreach (var (original, placed) in pairs)
        {
            var pinMap = OriginalToImportedPinNames[CellNameOfOriginal(original)];
            foreach (var (originalPinName, importedPinName) in pinMap)
            {
                var originalPin = original.PhysicalPins.First(p => p.Name == originalPinName);
                var placedPin = placed.PhysicalPins.First(p => p.Name == importedPinName);
                var (ox, oy) = originalPin.GetAbsolutePosition();
                var (px, py) = placedPin.GetAbsolutePosition();
                (px + dx).ShouldBe(ox, 1.0, $"pin {originalPinName}→{importedPinName} of {original.HumanReadableName} (X)");
                (py + dy).ShouldBe(oy, 1.0, $"pin {originalPinName}→{importedPinName} of {original.HumanReadableName} (Y)");
            }
        }
    }

    /// <summary>Every placed component renders (GDS outline polygons) and keeps its pins.</summary>
    private static void AssertEveryChildIsVisible(IReadOnlyList<Component> children, int? expectedPinsPerChild)
    {
        foreach (var child in children)
        {
            child.OutlinePolygons.ShouldNotBeNull().ShouldNotBeEmpty(
                $"every placed component keeps its GDS outline ({child.Identifier})");
            child.PhysicalPins.ShouldNotBeEmpty($"every placed component keeps its pins ({child.Identifier})");
            if (expectedPinsPerChild is not null)
                child.PhysicalPins.Count.ShouldBe(expectedPinsPerChild.Value,
                    $"the SiEPIC foundry cells and the demofab MMI are all 4-port devices ({child.Identifier})");
        }
    }

    // ── Netlist topology comparison ──────────────────────────────────────────

    /// <summary>
    /// SiEPIC-upgraded netlist comparison: the imported circuit's YAML netlist is a
    /// SUBSET of the original's topology — same instance census per component class,
    /// exactly the 5 route-derived edges (each a pin-exact edge of the original
    /// graph, zero miswired edges), and every originally-external port still
    /// external (the 8 user external ports are among the imported 18 ports:
    /// 28 pins − 5 restored edges × 2).
    /// </summary>
    private static void AssertNetlistTopologyUpgraded(DesignCanvasViewModel original, DesignCanvasViewModel imported)
    {
        var originalTopology = ParseTopology(DeriveYaml(original, "original"), mapOriginalPinNames: true);
        var importedTopology = ParseTopology(DeriveYaml(imported, "imported"), mapOriginalPinNames: false);

        importedTopology.InstanceCountsByClass.ShouldBe(originalTopology.InstanceCountsByClass,
            "same circuit: 2× mmi2x2_dp, 2× ebeam_crossing4, 1× adiabatic, 1× bdc, 1× halfring");

        originalTopology.Edges.Count.ShouldBe(10, "his ten waveguide connections");
        importedTopology.Edges.ShouldBe(new[]
        {
            // The two MMI braids (his connections 1 and 2).
            "mmi2x2_dp#0/a0 = mmi2x2_dp#1/a1",
            "mmi2x2_dp#0/a1 = mmi2x2_dp#1/a0",
            // Halfring↔adiabatic, bdc↔crossing and crossing↔crossing: the #888
            // largest-viable-radius arcs keep these chains clear of the junction.
            "ebeam_adiabatic_te1550#0/port 2 = ebeam_dc_halfring_straight#0/port 3",
            "ebeam_bdc_te1550#0/port 1 = ebeam_crossing4#1/port 2",
            "ebeam_crossing4#0/port 2 = ebeam_crossing4#1/port 1",
        }, ignoreOrder: true,
            customMessage: "exactly the five clean two-pin chains restore, pin-exact, nothing miswired");
        importedTopology.Edges.ShouldBeSubsetOf(originalTopology.Edges,
            "every restored edge is a real edge of the original circuit — no spurious topology");

        importedTopology.Ports.Count.ShouldBe(18, "28 pins minus the five restored edges");
        originalTopology.Ports.Count.ShouldBe(8, "his eight external ports");
        originalTopology.Ports.ShouldBeSubsetOf(importedTopology.Ports,
            "every originally-external pin stays external after the round trip");
    }

    /// <summary>
    /// Stub-scenario netlist comparison: same instance census; the two MMI braids
    /// restore route-derived (subset of the original edges, nothing miswired);
    /// everything else stays frozen junction geometry without netlist edges. The
    /// four MMI a-pins are occupied, so 42 pins surface as ports.
    /// </summary>
    private static void AssertNetlistTopologyStub(DesignCanvasViewModel original, DesignCanvasViewModel imported)
    {
        var originalTopology = ParseTopology(DeriveYaml(original, "original"), mapOriginalPinNames: true);
        var importedTopology = ParseTopology(DeriveYaml(imported, "imported"), mapOriginalPinNames: false);

        importedTopology.InstanceCountsByClass.ShouldBe(originalTopology.InstanceCountsByClass);
        importedTopology.Edges.ShouldBe(new[]
        {
            "mmi2x2_dp#0/a0 = mmi2x2_dp#1/a1",
            "mmi2x2_dp#0/a1 = mmi2x2_dp#1/a0",
        }, ignoreOrder: true, customMessage: "the two MMI braids restore route-derived, pin-exact");
        importedTopology.Edges.ShouldBeSubsetOf(originalTopology.Edges,
            "every restored edge is a real edge of the original circuit — no spurious topology");
        importedTopology.Ports.Count.ShouldBe(42, "46 pins minus the four occupied by the braids");
    }

    // ── Error-channel sanity ─────────────────────────────────────────────────

    /// <summary>
    /// Every message produced along the loop stays user-presentable: no raw
    /// exception text, no stack traces, no empty strings, no embedded newlines;
    /// the info channel carries no warnings. The "imported as frozen paths"
    /// note appears exactly once (in the import INFOS — frozen geometry is
    /// visible on the group, not silent data loss, so it is not a warning).
    /// </summary>
    private static void AssertMessageChannels(
        IReadOnlyList<string> skippedConnections,
        IReadOnlyList<string> exportWarnings,
        string exportStdErr,
        IReadOnlyList<string> importWarnings,
        IReadOnlyList<string> importInfos,
        IReadOnlyList<string> placementWarnings,
        IReadOnlyList<string> validationWarnings)
    {
        skippedConnections.ShouldBeEmpty("all 10 routes exported");
        exportWarnings.ShouldBeEmpty("nothing fell back to placeholder geometry");
        exportStdErr.ShouldNotContain("Traceback");

        var allMessages = importWarnings.Concat(importInfos)
            .Concat(placementWarnings).Concat(validationWarnings).ToList();
        allMessages.ShouldAllBe(m =>
                !string.IsNullOrWhiteSpace(m) &&
                !m.Contains("Exception", StringComparison.Ordinal) &&
                !m.Contains("Traceback", StringComparison.Ordinal) &&
                !m.Contains('\n'),
            "messages stay user-presentable single-line sentences");
        importInfos.ShouldAllBe(i => !i.Contains("WARN", StringComparison.Ordinal),
            "the info channel carries no warnings");
        allMessages.Count(m => m.Contains("imported as frozen paths (not re-routable)", StringComparison.Ordinal))
            .ShouldBe(1, "the frozen-route-paths note is produced exactly once");
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    /// <summary>Internal for <see cref="GdsReexportIdempotencyTests"/> (the re-export generation loop).</summary>
    internal sealed record ExportResult(
        DesignCanvasViewModel Canvas,
        string Script,
        string GdsPath,
        string Python,
        bool SiepicUpgraded,
        List<string> SkippedConnections,
        List<string> ExportWarnings,
        string StdErr);

    /// <summary>Instance wrapper over the shared static harness, bound to this fixture's temp root.</summary>
    private async Task<ExportResult> ExportUserDesignAsync(string subdir, bool stripSiepicUpgrade) =>
        await ExportUserDesignAsync(_root, subdir, stripSiepicUpgrade);

    /// <summary>
    /// Builds the user's design, exports it with the app's exporter and runs the
    /// script with real nazca. <paramref name="stripSiepicUpgrade"/> removes the
    /// klayout upgrade CALL (the def stays, unused) — exactly the GDS a
    /// bare-nazca python would write (stub boxes + (1,10) pin labels). Internal
    /// static with an explicit temp root so <see cref="GdsReexportIdempotencyTests"/>
    /// reuses the same harness.
    /// </summary>
    internal static async Task<ExportResult> ExportUserDesignAsync(string root, string subdir, bool stripSiepicUpgrade)
    {
        var python = await GdsUserDesignFixture.FindNazcaPythonAsync();
        Skip.If(python == null, "No Python with nazca available — the round trip needs the real engine.");

        var canvas = GdsUserDesignFixture.BuildUserDesignCanvas();
        var skippedConnections = new List<string>();
        var exportWarnings = new List<string>();
        var script = new SimpleNazcaExporter().Export(
            canvas, skippedConnections: skippedConnections, exportWarnings: exportWarnings);

        if (stripSiepicUpgrade)
        {
            script = string.Join('\n', script.Split('\n')
                .Where(l => !l.Contains("_lunima_upgrade_siepic_cells(gds_filename", StringComparison.Ordinal)));
            script.ShouldNotContain("_lunima_upgrade_siepic_cells(gds_filename");
        }

        var exportDir = Path.Combine(root, subdir);
        Directory.CreateDirectory(exportDir);
        var scriptPath = Path.Combine(exportDir, "user_design.py");
        await File.WriteAllTextAsync(scriptPath, script);
        var run = await SiepicRealGeometryExportTests.RunPythonAsync(python, exportDir, scriptPath);
        run.ExitCode.ShouldBe(0, $"nazca export script failed:\n{run.StdOut}\n{run.StdErr}");
        var gdsPath = Path.ChangeExtension(scriptPath, ".gds");
        File.Exists(gdsPath).ShouldBeTrue($"script did not write {gdsPath}:\n{run.StdOut}");

        var siepicUpgraded = run.StdOut.Contains("SiEPIC cell(s) upgraded", StringComparison.Ordinal);
        return new ExportResult(
            canvas, script, gdsPath, python, siepicUpgraded, skippedConnections, exportWarnings, run.StdErr);
    }

    /// <summary>Analyze + explode-import through the same service path the GDS-import button uses.</summary>
    private async Task<(GdsImportOutcome Outcome, GdsDesignScopeTestHost Host)> ImportExplodeAsync(
        string gdsPath)
    {
        var host = new GdsDesignScopeTestHost();
        _hosts.Add(host);
        return (await ImportExplodeAsync(host, gdsPath), host);
    }

    /// <summary>
    /// Analyze + explode-import through the same service path the GDS-import button uses.
    /// Internal static with an explicit host so <see cref="GdsReexportIdempotencyTests"/>
    /// reuses the same harness; <paramref name="templateProvider"/> overrides the known-component
    /// library the resolver sees (default: only this import's own registered templates).
    /// </summary>
    internal static async Task<GdsImportOutcome> ImportExplodeAsync(
        GdsDesignScopeTestHost host, string gdsPath, Func<IReadOnlyList<ComponentTemplate>>? templateProvider = null)
    {
        var analysis = await GdsImportService.AnalyzeAsync(gdsPath);
        analysis.TopCellCandidates.ShouldBe(new[] { "ConnectAPIC_Design" });
        analysis.TopCells.ShouldBe(new[] { new GdsTopCellSummary("ConnectAPIC_Design", 7) });

        var service = host.CreateService(templateProvider);
        var dialogOptions = new GdsHierarchyImportOptions
        {
            // The dialog's default port-layer field ("1,10;501,1").
            PinDetection = new GdsPinDetectionOptions { PortLayers = [(1, 10), (501, 1)] },
        };
        return await service.ImportAsync(gdsPath, analysis.TopCellCandidates[0], dialogOptions, null);
    }

    /// <summary>Runs a process through <see cref="ProcessLaunchFactory"/> — the app's external-tool launch pattern.</summary>
    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunViaFactoryAsync(
        string command, string workingDir, params string[] args)
    {
        var factory = ProcessLaunchFactory.CreateDefault();
        factory.TryBuild(command, args, workingDir, null, out var psi, out var error)
            .ShouldBeTrue(error);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        using var process = Process.Start(psi)!;
        var stdOut = process.StandardOutput.ReadToEndAsync();
        var stdErr = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"Process did not finish within 4 minutes: {command} {string.Join(' ', args)}");
        }
        return (process.ExitCode, await stdOut, await stdErr);
    }

    /// <summary>Probes which independent GDS reader the python has: "klayout", "gdstk", or null.</summary>
    private static async Task<string?> ProbeGdsEngineAsync(string python)
    {
        foreach (var (engine, import) in new[] { ("klayout", "klayout.db"), ("gdstk", "gdstk") })
        {
            var probe = await RunViaFactoryAsync(python, Path.GetTempPath(), "-c", $"import {import}");
            if (probe.ExitCode == 0)
                return engine;
        }
        return null;
    }

    /// <summary>Derives the gdsfactory YAML netlist of a canvas exactly like the Netlist panel does.
    /// Internal for <see cref="GdsReexportIdempotencyTests"/>.</summary>
    internal static string DeriveYaml(DesignCanvasViewModel canvas, string designName)
    {
        var netlist = new NetlistDeriver().Derive(
            canvas.Components.Select(vm => vm.Component),
            canvas.Connections.Select(vm => vm.Connection),
            designName);
        return new GdsFactoryYamlNetlistWriter().Write(netlist);
    }

    /// <summary>
    /// Parses a gdsfactory YAML netlist into a comparison topology: instance
    /// census per component class (instances ranked by placement X then Y),
    /// the connection graph as canonical normalized edge keys, and the external
    /// ports. <paramref name="mapOriginalPinNames"/> translates the original
    /// canvas's template pin names into the re-imported pin namespace (see the
    /// class summary for the documented mapping); the imported side is already
    /// in it.
    /// </summary>
    internal static NetlistTopology ParseTopology(string yaml, bool mapOriginalPinNames)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        var root = (YamlMappingNode)stream.Documents[0].RootNode;

        var componentByInstance = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in Children(root, "instances"))
            componentByInstance[Key(entry)] = ValueOf(entry.Value, "component");

        var rankByInstance = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var classGroup in componentByInstance
                     .GroupBy(kv => ClassKeyOf(kv.Value))
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var ranked = classGroup
                .Select(kv => kv.Key)
                .OrderBy(name => Placement(root, name, "x"))
                .ThenBy(name => Placement(root, name, "y"))
                .ToList();
            for (var i = 0; i < ranked.Count; i++)
                rankByInstance[ranked[i]] = i;
        }

        string Normalize(string instance, string pin)
        {
            var classKey = ClassKeyOf(componentByInstance[instance]);
            var mappedPin = mapOriginalPinNames
                ? OriginalToImportedPinNames[classKey][pin]
                : pin;
            return $"{classKey}#{rankByInstance[instance]}/{mappedPin}";
        }

        var edges = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in Children(root, "connections"))
        {
            var (instanceA, portA) = SplitEndpoint(Key(entry));
            var (instanceB, portB) = SplitEndpoint(Scalar(entry.Value));
            var a = Normalize(instanceA, portA);
            var b = Normalize(instanceB, portB);
            edges.Add(string.CompareOrdinal(a, b) <= 0 ? $"{a} = {b}" : $"{b} = {a}");
        }

        var ports = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in Children(root, "ports"))
        {
            var (instance, pin) = SplitEndpoint(Scalar(entry.Value));
            ports.Add(Normalize(instance, pin));
        }

        var instanceCounts = componentByInstance
            .GroupBy(kv => ClassKeyOf(kv.Value))
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return new NetlistTopology(instanceCounts, edges, ports);
    }

    internal sealed record NetlistTopology(
        Dictionary<string, int> InstanceCountsByClass,
        HashSet<string> Edges,
        HashSet<string> Ports);

    /// <summary>
    /// Maps a netlist component reference to the comparison class key (the GDS
    /// cell name): strips the fabricated "nazca_" prefix of imported raw-code
    /// drafts and the "demo." module qualifier of the original demo-PDK
    /// components, and folds the halfring's parameter-hash stub cell name back
    /// to the plain cell name.
    /// </summary>
    internal static string ClassKeyOf(string componentRef)
    {
        var name = componentRef;
        if (name.StartsWith("nazca_", StringComparison.Ordinal))
            name = name["nazca_".Length..];
        else if (name.StartsWith("demo.", StringComparison.Ordinal))
            name = name["demo.".Length..];
        return name == "ebeam_dc_halfring_straight_7357fa" ? "ebeam_dc_halfring_straight" : name;
    }

    /// <summary>The original template pin name → re-imported pin name per component class (see class summary).
    /// Internal for <see cref="GdsReexportIdempotencyTests"/>.</summary>
    internal static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> OriginalToImportedPinNames =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["mmi2x2_dp"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["in1"] = "a0", ["in2"] = "a1", ["out1"] = "b0", ["out2"] = "b1",
            },
            ["ebeam_adiabatic_te1550"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // Since #811 the upgraded cells carry the app pin names verbatim.
                ["port 1"] = "port 1", ["port 2"] = "port 2", ["port 3"] = "port 3", ["port 4"] = "port 4",
            },
            ["ebeam_bdc_te1550"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["port 1"] = "port 1", ["port 2"] = "port 2", ["port 3"] = "port 3", ["port 4"] = "port 4",
            },
            ["ebeam_crossing4"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["port 1"] = "port 1", ["port 2"] = "port 2", ["port 3"] = "port 3", ["port 4"] = "port 4",
            },
            ["ebeam_dc_halfring_straight"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["port 1"] = "port 1", ["port 2"] = "port 2", ["port 3"] = "port 3", ["port 4"] = "port 4",
            },
        };

    /// <summary>The original canvas template name → GDS cell name.</summary>
    internal static string CellNameOfOriginal(Component original) => original.HumanReadableName switch
    {
        "2x2 MMI Coupler" => "mmi2x2_dp",
        "Adiabatic Coupler TE 1550" => "ebeam_adiabatic_te1550",
        "Broadband DC TE 1550" => "ebeam_bdc_te1550",
        "Crossing 4-Port" => "ebeam_crossing4",
        "DC Halfring-Straight" => "ebeam_dc_halfring_straight",
        var other => throw new InvalidOperationException($"unmapped template '{other}'"),
    };

    /// <summary>
    /// Pairs each original component with the placed re-imported child of the
    /// same component class by position rank (X then Y): counts per class match
    /// (asserted) and within a class the source layout's relative order is
    /// preserved by the export.
    /// </summary>
    internal static IReadOnlyList<(Component original, Component placed)> PairByClassAndRank(
        DesignCanvasViewModel originalCanvas, IReadOnlyList<Component> placedChildren)
    {
        var originals = originalCanvas.Components.Select(vm => vm.Component).ToList();
        var pairs = new List<(Component, Component)>();
        foreach (var classGroup in originals.GroupBy(CellNameOfOriginal))
        {
            var rankedOriginals = classGroup
                .OrderBy(c => c.PhysicalX).ThenBy(c => c.PhysicalY).ToList();
            var rankedPlaced = placedChildren
                .Where(c => ClassKeyOf(c.HumanReadableName ?? string.Empty) == classGroup.Key)
                .OrderBy(c => c.PhysicalX).ThenBy(c => c.PhysicalY).ToList();
            rankedPlaced.Count.ShouldBe(rankedOriginals.Count,
                $"every original {classGroup.Key} was placed exactly once");
            pairs.AddRange(rankedOriginals.Zip(rankedPlaced));
        }
        pairs.Count.ShouldBe(7);
        return pairs;
    }

    // ── YAML helpers ─────────────────────────────────────────────────────────

    private static IEnumerable<KeyValuePair<YamlNode, YamlNode>> Children(YamlNode node, string key) =>
        node is YamlMappingNode mapping && mapping.Children.TryGetValue(new YamlScalarNode(key), out var child)
            ? (YamlMappingNode)child
            : Enumerable.Empty<KeyValuePair<YamlNode, YamlNode>>();

    private static string Key(KeyValuePair<YamlNode, YamlNode> entry) => ((YamlScalarNode)entry.Key).Value!;
    private static string Scalar(YamlNode node) => ((YamlScalarNode)node).Value!;
    private static string ValueOf(YamlNode node, string key) => Scalar(((YamlMappingNode)node)[new YamlScalarNode(key)]);

    private static double Placement(YamlMappingNode root, string instanceName, string axis) =>
        double.Parse(
            ValueOf(Children(root, "placements").Single(e => Key(e) == instanceName).Value, axis),
            CultureInfo.InvariantCulture);

    private static (string Instance, string Pin) SplitEndpoint(string endpoint)
    {
        var comma = endpoint.IndexOf(',', StringComparison.Ordinal);
        return (endpoint[..comma], endpoint[(comma + 1)..]);
    }
}
