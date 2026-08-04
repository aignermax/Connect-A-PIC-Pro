using System.Diagnostics;
using System.Globalization;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.ViewModels.Canvas;
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
/// button's service path (<see cref="GdsImportService"/> + <see cref="GdsPlacementExecutor"/>
/// with auto-connect), and finally compared as a NETLIST (gdsfactory YAML, parsed with
/// YamlDotNet) against the original canvas — topology, not names.
/// <para>
/// Pinned loop numbers (SiEPIC-upgraded scenario — klayout + siepic_ebeam_pdk present,
/// i.e. the Lunima managed env and CI): the export writes 7 top-cell references over
/// 5 device cells; the re-import registers 5 drafts and places 7 instances at the
/// original coordinates (uniform origin shift, ≤1 µm slack from the real foundry
/// cells' bounding boxes); the abutment matcher reconstructs 0 connections (his 10
/// connections were routed waveguides — flattened top-cell geometry, no abutting
/// pins); the auto-connect pass restores exactly 2 of the 10 logical connections
/// (the two short straight opposing-pin spans: crossing↔crossing 12.8 µm and
/// halfring↔adiabatic 10.6 µm) and vetoes 2 more as genuinely ambiguous
/// (see below); the other 6 are unrestorable by an opposing-pin heuristic:
/// connections 1–2 join same-direction MMI input pins (both point west — never
/// opposing) and connections 3–6 join an east MMI output to a north/south
/// crossing pin (90° off — never opposing). The ambiguity vetoes: connection 8
/// (crossing872 east pin → bdc west pins: the bdc's two west pins are 0.09 µm
/// equidistant from it) and connection 9 (crossing1175 west pin ← adiabatic east
/// pins: 0.39 µm apart) — guessing there would wire the WRONG pin, so the pass
/// honestly refuses. The imported netlist is therefore a SUBSET of the original
/// topology: 2 of 10 edges, pin-exact, zero miswired edges.
/// </para>
/// <para>
/// Pin-name mapping used for the topology comparison (original template pin →
/// re-imported pin, verified GEOMETRICALLY against the placed pins within 1 µm):
/// demofab MMI in1/in2/out1/out2 → a0/a1/b0/b1 (its (501,1) bb_pin_text labels);
/// ebeam cells "port 1..4" → the SiEPIC foundry port names: adiabatic/bdc
/// port1→opt1, port2→opt2, port3→opt4, port4→opt3 (the foundry numbers the east
/// side top-down, the app template bottom-up); crossing port1→opt, port2→opt4,
/// port3→opt2, port4→opt3 (the west port is literally "opt"); halfring
/// port1..4→pin1..4. Component-class mapping for the netlist instances:
/// "demo.mmi2x2_dp" (module-qualified nazca name) and the imported drafts'
/// fabricated "nazca_&lt;cellname&gt;" (raw-code components carry no nazca
/// function — a documented convention, see GdsCellDraftMapperTests) both map to
/// the GDS cell name; the halfring's parameter-hash stub cell name
/// (ebeam_dc_halfring_straight_7357fa) maps back to ebeam_dc_halfring_straight.
/// </para>
/// <para>
/// The stub scenario (bare-nazca python: the four ebeam cells stay 1-polygon stub
/// boxes) is forced in <see cref="FullLoop_StubScenario_AutoConnectRestoresNothingButNeverMiswires"/>
/// by stripping the klayout upgrade call from the export script — the same GDS a
/// bare-nazca environment would produce. There the heuristic edge pins
/// (<c>heur_N</c>) sit within a µm of their labeled twins, so EVERY candidate
/// pairing dies of the ambiguity guard: 0/10 restored, but also zero miswired
/// connections — the honest v1 outcome.
/// </para>
/// </summary>
[Trait("Category", "Slow")]
public class GdsHighestLevelRoundTripTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lunima-gds-highlevel-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    // ── The full loop ────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task FullLoop_UserDesign_ExportReimportAutoConnect_NetlistTopologyMatchesOriginal()
    {
        // ── 1+2. Export the user's design with the app's exporter and run it ──
        var export = await ExportUserDesignAsync("export", stripSiepicUpgrade: false);
        export.SkippedConnections.ShouldBeEmpty("all 10 routes are real, exportable geometry");
        export.ExportWarnings.ShouldBeEmpty();
        GdsUserDesignFixture.CountLines(export.Script, ".put('org',").ShouldBe(7,
            "the seven canvas components are placed in the export script");

        // ── 3. Analyze + explode-import through the button's service path ──
        var (outcome, sink) = await ImportExplodeAsync(export.GdsPath, "loop");
        outcome.RegisteredComponents.Select(r => r.CellDraftName).ShouldBe(new[]
        {
            "mmi2x2_dp", "ebeam_adiabatic_te1550", "ebeam_bdc_te1550",
            "ebeam_crossing4", "ebeam_dc_halfring_straight_7357fa",
        });
        outcome.Instances.Count.ShouldBe(7);

        // The honest v1 connection outcome: his layout is SPACED — the 10 logical
        // connections are routed waveguides that nazca flattens into top-cell
        // polygons, so no two component pins abut and the abutment matcher finds
        // nothing. Exactly one user-presentable warning says so — and the
        // flattened routes come back as frozen, non-re-routable paths.
        outcome.Connections.ShouldBeEmpty();
        var warning = outcome.Warnings.ShouldHaveSingleItem();
        warning.ShouldContain("imported as frozen paths (not re-routable)");
        outcome.TopCellWaveguidePolygons.ShouldNotBeEmpty(
            "the flattened routes land on the waveguide layer");

        // ── 4. Place + auto-connect (executor default radius 200 µm) ──
        var canvas2 = new DesignCanvasViewModel();
        canvas2.InitializeAStarRouting(150, -700, 950, -250);
        var report = await new GdsPlacementExecutor(canvas2, null, () => sink.Templates.ToList())
            .ExecuteAsync(GdsPlacementPlan.FromOutcome(outcome), autoConnectFreePins: true);

        report.PlacedCount.ShouldBe(7);
        report.SkippedPlacements.ShouldBeEmpty();
        report.ConnectedCount.ShouldBe(0, "no abutment connections exist in this spaced design");
        report.Warnings.ShouldBeEmpty();
        report.ValidationWarnings.ShouldBeEmpty("the restored routes are short and clean");
        report.GroupCreated.ShouldBeTrue();
        report.GroupName.ShouldBe("ConnectAPIC_Design");

        var group = canvas2.Components.ShouldHaveSingleItem().Component.ShouldBeOfType<ComponentGroup>();
        var children = group.GetAllComponentsRecursive().ToList();
        children.Count.ShouldBe(7);

        if (export.SiepicUpgraded)
        {
            AssertAutoConnectUpgraded(report);
            AssertPlacementsMatchOriginals(export.Canvas, children, positionToleranceUm: 1.0);
            AssertPinMappingGeometrically(export.Canvas, children);
            AssertEveryChildIsVisible(children, expectedPinsPerChild: 4);
            AssertNetlistTopologyUpgraded(export.Canvas, canvas2);
        }
        else
        {
            // Bare-nazca environment: the same outcome the forced-stub test pins.
            AssertAutoConnectStub(report);
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
    /// labeled ones. The auto-connect pass must then restore NOTHING — every
    /// candidate pair is ambiguous (labeled pin vs. its heur twin) — while never
    /// wiring a single wrong connection.
    /// </summary>
    [SkippableFact]
    public async Task FullLoop_StubScenario_AutoConnectRestoresNothingButNeverMiswires()
    {
        var export = await ExportUserDesignAsync("export-stub", stripSiepicUpgrade: true);
        export.SiepicUpgraded.ShouldBeFalse("the upgrade call was stripped — the stubs survive");

        var (outcome, sink) = await ImportExplodeAsync(export.GdsPath, "stub");
        outcome.Instances.Count.ShouldBe(7);
        outcome.Connections.ShouldBeEmpty();
        outcome.Warnings.ShouldHaveSingleItem().ShouldContain("imported as frozen paths (not re-routable)");

        var canvas2 = new DesignCanvasViewModel();
        canvas2.InitializeAStarRouting(150, -700, 950, -250);
        var report = await new GdsPlacementExecutor(canvas2, null, () => sink.Templates.ToList())
            .ExecuteAsync(GdsPlacementPlan.FromOutcome(outcome), autoConnectFreePins: true);

        report.PlacedCount.ShouldBe(7);
        report.SkippedPlacements.ShouldBeEmpty();
        report.ConnectedCount.ShouldBe(0);
        report.Warnings.ShouldBeEmpty();
        report.ValidationWarnings.ShouldBeEmpty();
        report.GroupCreated.ShouldBeTrue();

        var group = canvas2.Components.ShouldHaveSingleItem().Component.ShouldBeOfType<ComponentGroup>();
        var children = group.GetAllComponentsRecursive().ToList();
        children.Count.ShouldBe(7);

        AssertAutoConnectStub(report);
        AssertPlacementsMatchOriginals(export.Canvas, children, positionToleranceUm: 1.0);
        AssertEveryChildIsVisible(children, expectedPinsPerChild: null);
        AssertNetlistTopologyStub(export.Canvas, canvas2);
        AssertMessageChannels(
            export.SkippedConnections, export.ExportWarnings, export.StdErr,
            outcome.Warnings, outcome.Infos, report.Warnings, report.ValidationWarnings);
    }

    // ── Stage assertions ─────────────────────────────────────────────────────

    /// <summary>
    /// The SiEPIC-upgraded auto-connect outcome: exactly 2 of the 10 logical
    /// connections restored pin-exact, 2 vetoed as genuinely ambiguous, the rest
    /// skipped with reasons — see the class summary for the per-connection
    /// accounting. Radius is the executor default (200 µm).
    /// </summary>
    private static void AssertAutoConnectUpgraded(GdsPlacementReport report)
    {
        report.AutoConnectedCount.ShouldBe(2,
            "connections 7 (crossing↔crossing, 12.8 µm) and 10 (halfring↔adiabatic, 10.6 µm) " +
            "are the only short straight opposing-pin spans of his layout");

        var pairAssertions = new[]
        {
            new[] { "'ebeam_crossing4#0.opt'", "'ebeam_crossing4#1.opt4'" },       // connection 7
            new[] { "'ebeam_adiabatic_te1550#0.opt2'", "'ebeam_dc_halfring_straight_7357fa#0.pin3'" }, // connection 10
        };
        foreach (var labels in pairAssertions)
        {
            report.AutoConnectedPairs.ShouldContain(
                p => p.Contains(labels[0], StringComparison.Ordinal) &&
                     p.Contains(labels[1], StringComparison.Ordinal),
                $"the restored pair {labels[0]} ↔ {labels[1]}");
        }

        // The two ambiguity vetoes, per-pin (below the summary-collapse cap):
        // connection 8 (crossing872's east pin sees the bdc's two west pins
        // 0.09 µm apart) and connection 9 (crossing1175's west pin sees the
        // adiabatic's two east pins 0.39 µm apart). Guessing would miswire.
        report.SkippedAutoConnect.Count(s => s.Contains("'ebeam_crossing4#0.opt4'", StringComparison.Ordinal))
            .ShouldBe(1, "connection 8 is honestly refused: 178.75 vs 178.85 µm — a coin flip");
        report.SkippedAutoConnect.Count(s => s.Contains("'ebeam_crossing4#1.opt'", StringComparison.Ordinal))
            .ShouldBe(1, "connection 9 is honestly refused: 25.55 vs 25.94 µm — a coin flip");

        // Skip accounting: 28 pins total, 4 paired, 24 skipped — 2 ambiguous
        // (above), 4 not-facing (the crossings' north/south pins see the MMI
        // output pins opposing but perpendicular), 18 with no opposing partner
        // in radius (collapsed into one summary line, over the detail cap of 5).
        report.SkippedAutoConnect.Count.ShouldBe(7,
            "2 ambiguous + 4 not-facing (detailed) + 1 collapsed no-partner summary line");
        report.SkippedAutoConnect.ShouldContain(s => s.Contains("'ebeam_crossing4#0.opt2'", StringComparison.Ordinal));
        report.SkippedAutoConnect.ShouldContain(s => s.Contains("'ebeam_crossing4#0.opt3'", StringComparison.Ordinal));
        report.SkippedAutoConnect.ShouldContain(s => s.Contains("'ebeam_crossing4#1.opt2'", StringComparison.Ordinal));
        report.SkippedAutoConnect.ShouldContain(s => s.Contains("'ebeam_crossing4#1.opt3'", StringComparison.Ordinal));
    }

    /// <summary>
    /// The stub-scenario auto-connect outcome: 0/10 restored — the heuristic
    /// <c>heur_N</c> edge pins sit within a µm of their labeled twins, so every
    /// candidate pair (labeled pin vs. twin) dies of the ambiguity guard. No
    /// miswired connection is created either: all 46 pins (28 labeled + 18
    /// heuristic) are accounted for as skips in three collapsed summary lines.
    /// </summary>
    private static void AssertAutoConnectStub(GdsPlacementReport report)
    {
        report.AutoConnectedCount.ShouldBe(0,
            "every candidate pair is ambiguous: each labeled pin has a heur twin within a µm");
        report.AutoConnectedPairs.ShouldBeEmpty();
        report.SkippedAutoConnect.Count.ShouldBe(3,
            "all three skip reasons exceed the detail cap of 5 and collapse into summary lines: " +
            "10 ambiguous, 28 no-partner-in-radius, 8 not-facing = 46 pins (28 labeled + 18 heuristic)");
        report.ValidationWarnings.ShouldBeEmpty();
    }

    /// <summary>
    /// Every placed child sits at its original component's position modulo the
    /// uniform origin shift (the import re-origins at the layout's top-left,
    /// which includes the routed waveguides extending past the components).
    /// The shift is anchored on the exact mmi2x2_dp placement; the SiEPIC cells
    /// add ≤0.5 µm of bounding-box slack (real foundry cell vs. app template).
    /// Pairing is by component class + position rank.
    /// </summary>
    private static void AssertPlacementsMatchOriginals(
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
    /// exactly the 2 auto-restored edges (both pin-exact edges of the original
    /// graph, zero miswired edges), and every originally-external port still
    /// external (the 8 user external ports are among the imported 24 ports:
    /// 28 pins − 2 restored edges × 2).
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
            "ebeam_adiabatic_te1550#0/opt2 = ebeam_dc_halfring_straight#0/pin3",
            "ebeam_crossing4#0/opt4 = ebeam_crossing4#1/opt",
        }, ignoreOrder: true,
            customMessage: "exactly connections 7 and 10 restored, pin-exact, nothing miswired");
        importedTopology.Edges.ShouldBeSubsetOf(originalTopology.Edges,
            "every restored edge is a real edge of the original circuit — no spurious topology");

        importedTopology.Ports.Count.ShouldBe(24, "28 pins minus the two restored edges");
        originalTopology.Ports.Count.ShouldBe(8, "his eight external ports");
        originalTopology.Ports.ShouldBeSubsetOf(importedTopology.Ports,
            "every originally-external pin stays external after the round trip");
    }

    /// <summary>
    /// Stub-scenario netlist comparison: same instance census, but ZERO edges
    /// (every pairing vetoed as ambiguous among the heur twins) — the imported
    /// netlist is honestly disconnected rather than miswired. All 46 pins
    /// surface as ports.
    /// </summary>
    private static void AssertNetlistTopologyStub(DesignCanvasViewModel original, DesignCanvasViewModel imported)
    {
        var originalTopology = ParseTopology(DeriveYaml(original, "original"), mapOriginalPinNames: true);
        var importedTopology = ParseTopology(DeriveYaml(imported, "imported"), mapOriginalPinNames: false);

        importedTopology.InstanceCountsByClass.ShouldBe(originalTopology.InstanceCountsByClass);
        importedTopology.Edges.ShouldBeEmpty("the ambiguity guard refuses every twin-poisoned pair");
        importedTopology.Ports.Count.ShouldBe(46, "28 labeled pins + 18 heuristic edge pins, all unconnected");
    }

    // ── Error-channel sanity ─────────────────────────────────────────────────

    /// <summary>
    /// Every message produced along the loop stays user-presentable: no raw
    /// exception text, no stack traces, no empty strings, no embedded newlines;
    /// the info channel carries no warnings. The "imported as frozen paths"
    /// warning appears exactly once (in the import warnings).
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

        importInfos.ShouldBeEmpty("every cell was unknown to the empty sink — nothing resolved, nothing skipped");
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
            .ShouldBe(1, "the frozen-route-paths warning is produced exactly once");
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private sealed record ExportResult(
        DesignCanvasViewModel Canvas,
        string Script,
        string GdsPath,
        string Python,
        bool SiepicUpgraded,
        List<string> SkippedConnections,
        List<string> ExportWarnings,
        string StdErr);

    /// <summary>
    /// Builds the user's design, exports it with the app's exporter and runs the
    /// script with real nazca. <paramref name="stripSiepicUpgrade"/> removes the
    /// klayout upgrade CALL (the def stays, unused) — exactly the GDS a
    /// bare-nazca python would write (stub boxes + (1,10) pin labels).
    /// </summary>
    private async Task<ExportResult> ExportUserDesignAsync(string subdir, bool stripSiepicUpgrade)
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

        var exportDir = Path.Combine(_root, subdir);
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
    private async Task<(GdsImportOutcome Outcome, GdsUserDesignFixture.LibrarySink Sink)> ImportExplodeAsync(
        string gdsPath, string tag)
    {
        var analysis = await GdsImportService.AnalyzeAsync(gdsPath);
        analysis.TopCellCandidates.ShouldBe(new[] { "ConnectAPIC_Design" });
        analysis.TopCells.ShouldBe(new[] { new GdsTopCellSummary("ConnectAPIC_Design", 7) });

        var sink = new GdsUserDesignFixture.LibrarySink(Path.Combine(_root, $"prefs-{tag}.json"));
        var service = new GdsImportService(
            GdsUserDesignFixture.CreateStore(_root, $"pdks-{tag}"), () => sink.Templates.ToList(), sink.Register);
        var dialogOptions = new GdsHierarchyImportOptions
        {
            // The dialog's default port-layer field ("1,10;501,1").
            PinDetection = new GdsPinDetectionOptions { PortLayers = [(1, 10), (501, 1)] },
        };
        var outcome = await service.ImportAsync(gdsPath, analysis.TopCellCandidates[0], dialogOptions, null);
        return (outcome, sink);
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

    /// <summary>Derives the gdsfactory YAML netlist of a canvas exactly like the Netlist panel does.</summary>
    private static string DeriveYaml(DesignCanvasViewModel canvas, string designName)
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
    private static NetlistTopology ParseTopology(string yaml, bool mapOriginalPinNames)
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

    private sealed record NetlistTopology(
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
    private static string ClassKeyOf(string componentRef)
    {
        var name = componentRef;
        if (name.StartsWith("nazca_", StringComparison.Ordinal))
            name = name["nazca_".Length..];
        else if (name.StartsWith("demo.", StringComparison.Ordinal))
            name = name["demo.".Length..];
        return name == "ebeam_dc_halfring_straight_7357fa" ? "ebeam_dc_halfring_straight" : name;
    }

    /// <summary>The original template pin name → re-imported pin name per component class (see class summary).</summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> OriginalToImportedPinNames =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["mmi2x2_dp"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["in1"] = "a0", ["in2"] = "a1", ["out1"] = "b0", ["out2"] = "b1",
            },
            ["ebeam_adiabatic_te1550"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["port 1"] = "opt1", ["port 2"] = "opt2", ["port 3"] = "opt4", ["port 4"] = "opt3",
            },
            ["ebeam_bdc_te1550"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["port 1"] = "opt1", ["port 2"] = "opt2", ["port 3"] = "opt4", ["port 4"] = "opt3",
            },
            ["ebeam_crossing4"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["port 1"] = "opt", ["port 2"] = "opt4", ["port 3"] = "opt2", ["port 4"] = "opt3",
            },
            ["ebeam_dc_halfring_straight"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["port 1"] = "pin1", ["port 2"] = "pin2", ["port 3"] = "pin3", ["port 4"] = "pin4",
            },
        };

    /// <summary>The original canvas template name → GDS cell name.</summary>
    private static string CellNameOfOriginal(Component original) => original.HumanReadableName switch
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
    private static IReadOnlyList<(Component original, Component placed)> PairByClassAndRank(
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
