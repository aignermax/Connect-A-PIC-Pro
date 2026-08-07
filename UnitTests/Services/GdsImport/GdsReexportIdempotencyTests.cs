using CAP.Avalonia.Services;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Core;
using Shouldly;
using UnitTests.Export;
using Xunit;

namespace UnitTests.Services.GdsImport;

/// <summary>
/// End-to-end round-trip IDEMPOTENCY test — the user's real arc continued one
/// generation further than <see cref="GdsHighestLevelRoundTripTests"/>: his
/// 7-component mixed-PDK design (<see cref="GdsUserDesignFixture"/>) is exported
/// (real nazca) and re-imported with frozen geometry (generation 1); then the
/// IMPORTED design is exported AGAIN — a file whose top-cell routing now comes
/// from generation 1's frozen-path emissions — and THAT file is imported and
/// placed (generation 2). Re-importing our own re-export must be stable:
/// <list type="bullet">
/// <item>same connection census (connected / route-derived / cached-route counts),</item>
/// <item>same frozen-path census (pin-less junction outlines + pinned frozen routes),</item>
/// <item>same per-component positions (one uniform re-origin shift, no per-component drift),</item>
/// <item>same netlist topology (instance census, edges, ports),</item>
/// <item>no growing polygon counts (neither top-cell route polygons nor child outlines).</item>
/// </list>
/// This is exactly where "the same waveguide came in twice" (doubled route
/// geometry) would surface: a consumed route polygon that also stays a frozen
/// outline, or a frozen path whose re-export multiplies, shows up here as a
/// growing census.
/// </summary>
[Trait("Category", "Slow")]
public class GdsReexportIdempotencyTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lunima-gds-reexport-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [SkippableFact]
    public async Task ReexportedImport_Generation2_MatchesGeneration1()
    {
        var python = await GdsUserDesignFixture.FindNazcaPythonAsync();
        Skip.If(python == null, "No Python with nazca available — the round trip needs the real engine.");

        // ── Generation 1: the exact round-trip arc (frozen placement — the
        // deterministic, router-independent mode the netlist comparisons use) ──
        var export1 = await GdsHighestLevelRoundTripTests.ExportUserDesignAsync(
            _root, "export1", stripSiepicUpgrade: false);
        var (outcome1, sink1) = await GdsHighestLevelRoundTripTests.ImportExplodeAsync(
            _root, export1.GdsPath, "gen1");
        var canvas1 = new DesignCanvasViewModel();
        canvas1.InitializeAStarRouting(150, -700, 950, -250);
        var report1 = await new GdsPlacementExecutor(canvas1, null, () => sink1.Templates.ToList())
            .ExecuteAsync(GdsPlacementPlan.FromOutcome(outcome1), rerouteImportedConnections: false);
        report1.PlacedCount.ShouldBe(7);
        report1.GroupCreated.ShouldBeTrue();
        var group1 = canvas1.Components.ShouldHaveSingleItem().Component.ShouldBeOfType<ComponentGroup>();
        var children1 = group1.GetAllComponentsRecursive().ToList();
        children1.Count.ShouldBe(7);

        // ── Generation 2: export the IMPORTED design (its top-cell routing is now
        // written from generation 1's frozen paths) and import THAT ──
        var skipped2 = new List<string>();
        var warnings2 = new List<string>();
        var script2 = new SimpleNazcaExporter().Export(canvas1,
            skippedConnections: skipped2, exportWarnings: warnings2, library: sink1.Templates);
        skipped2.ShouldBeEmpty("every generation-1 frozen path and connection exports as real geometry");
        warnings2.ShouldBeEmpty("the generation-1 raw-code sources still exist inside the test's store");
        var gds2Path = await RunScriptAsync(python, "export2", script2);

        // The resolver sees generation 1's library — the user's session still has
        // yesterday's import registered when he re-imports the re-export.
        var (outcome2, sink2) = await GdsHighestLevelRoundTripTests.ImportExplodeAsync(
            _root, gds2Path, "gen2", templateProvider: () => sink1.Templates.ToList());
        var canvas2 = new DesignCanvasViewModel();
        canvas2.InitializeAStarRouting(150, -700, 950, -250);
        var report2 = await new GdsPlacementExecutor(canvas2, null, () => sink2.Templates.ToList())
            .ExecuteAsync(GdsPlacementPlan.FromOutcome(outcome2), rerouteImportedConnections: false);
        report2.PlacedCount.ShouldBe(7);
        report2.GroupCreated.ShouldBeTrue();
        var group2 = canvas2.Components.ShouldHaveSingleItem().Component.ShouldBeOfType<ComponentGroup>();
        var children2 = group2.GetAllComponentsRecursive().ToList();
        children2.Count.ShouldBe(7);

        // ── Connection census: no duplicated, lost or resurrected connections ──
        report2.ConnectedCount.ShouldBe(report1.ConnectedCount,
            "generation 2 restores exactly the connections generation 1 had — nothing doubled, nothing lost");
        report2.RouteDerivedCount.ShouldBe(report1.RouteDerivedCount);
        report2.CachedRouteCount.ShouldBe(report1.CachedRouteCount,
            "the restored chains keep their drawn geometry as frozen cached routes in both generations");
        report2.ReroutedCount.ShouldBe(0, "frozen mode hands nothing to the live router");
        report2.Warnings.ShouldBeEmpty();

        // ── Frozen-path census: the pin-less junction outlines and the pinned
        // route-derived frozen routes must not multiply across generations ──
        var pinless1 = group1.InternalPaths.Where(p => p.StartPin is null).ToList();
        var pinned1 = group1.InternalPaths.Where(p => p.StartPin is not null).ToList();
        var pinless2 = group2.InternalPaths.Where(p => p.StartPin is null).ToList();
        var pinned2 = group2.InternalPaths.Where(p => p.StartPin is not null).ToList();
        pinned2.Count.ShouldBe(pinned1.Count,
            "the route-derived connections stay connections — no extra frozen routes appeared");
        pinless2.Count.ShouldBe(pinless1.Count,
            "no duplicated route polygons: a polygon consumed by a connection must never " +
            "also render as a frozen outline, and a re-exported frozen path must not multiply");
        outcome2.TopCellWaveguidePolygons.Count.ShouldBe(outcome1.TopCellWaveguidePolygons.Count,
            "the re-export must not multiply the top-cell routing geometry");

        // ── Geometry: child outlines and positions do not drift ──
        var pairs = PairAcrossGenerations(children1, children2);
        foreach (var (gen1, gen2) in pairs)
        {
            gen2.OutlinePolygons.ShouldNotBeNull().Count.ShouldBe(gen1.OutlinePolygons!.Count,
                $"{gen1.HumanReadableName}: the device outline does not grow across generations");
        }
        AssertPositionsStable(pairs, positionToleranceUm: 1.0);

        // ── Netlist topology: generation 2 == generation 1 (both already in the
        // imported pin namespace — no original-name mapping) ──
        var topology1 = NormalizeTopology(GdsHighestLevelRoundTripTests.ParseTopology(
            GdsHighestLevelRoundTripTests.DeriveYaml(canvas1, "gen1"), mapOriginalPinNames: false));
        var topology2 = NormalizeTopology(GdsHighestLevelRoundTripTests.ParseTopology(
            GdsHighestLevelRoundTripTests.DeriveYaml(canvas2, "gen2"), mapOriginalPinNames: false));
        topology2.InstanceCountsByClass.ShouldBe(topology1.InstanceCountsByClass,
            "same circuit in both generations");
        topology2.Edges.ShouldBe(topology1.Edges, ignoreOrder: true,
            customMessage: "the re-exported file restores the same netlist edges — none doubled, none invented");
        topology2.Ports.ShouldBe(topology1.Ports, ignoreOrder: true,
            customMessage: "the free-pin census round-trips — a doubled connection would eat two ports");

        // ── Absolute pins per generation (the equality assertions above are only
        // meaningful against a known-good generation 1 — the same numbers
        // GdsHighestLevelRoundTripTests pins for the single round trip) ──
        pinless1.Count.ShouldBe(45,
            "the junction network's polygons ride the group as frozen paths (both engine scenarios)");
        report1.FrozenRoutePathCount.ShouldBe(45);
        outcome1.TopCellWaveguidePolygons.Count.ShouldBe(45);
        var expectedConnections = export1.SiepicUpgraded ? 4 : 2;
        report1.ConnectedCount.ShouldBe(expectedConnections,
            "the clean two-pin route chains restore route-derived (4 SiEPIC-upgraded: 2 MMI braids, " +
            "crossing↔crossing, halfring↔adiabatic; 2 bare-nazca: the MMI braids only)");
        report1.CachedRouteCount.ShouldBe(expectedConnections);
        pinned1.Count.ShouldBe(expectedConnections);
        topology1.Edges.Count.ShouldBe(expectedConnections);
        topology1.Ports.Count.ShouldBe(export1.SiepicUpgraded ? 20 : 42,
            "28 labeled pins minus the restored edges × 2 (bare-nazca: 46 incl. heuristic pins)");
    }

    /// <summary>Runs one exported nazca script and returns the produced .gds path.</summary>
    private async Task<string> RunScriptAsync(string python, string subdir, string script)
    {
        var exportDir = Path.Combine(_root, subdir);
        Directory.CreateDirectory(exportDir);
        var scriptPath = Path.Combine(exportDir, "user_design.py");
        await File.WriteAllTextAsync(scriptPath, script);
        var run = await SiepicRealGeometryExportTests.RunPythonAsync(python, exportDir, scriptPath);
        run.ExitCode.ShouldBe(0, $"nazca export script failed:\n{run.StdOut}\n{run.StdErr}");
        var gdsPath = Path.ChangeExtension(scriptPath, ".gds");
        File.Exists(gdsPath).ShouldBeTrue($"script did not write {gdsPath}:\n{run.StdOut}");
        return gdsPath;
    }

    /// <summary>
    /// Pairs generation-1 and generation-2 children by component class + position
    /// rank (X then Y) — the round-trip test's rank pairing across generations.
    /// </summary>
    private static IReadOnlyList<(Component Gen1, Component Gen2)> PairAcrossGenerations(
        IReadOnlyList<Component> children1, IReadOnlyList<Component> children2)
    {
        var pairs = new List<(Component, Component)>();
        foreach (var classGroup in children1.GroupBy(c => StableClassKeyOf(c.HumanReadableName ?? string.Empty)))
        {
            var ranked1 = classGroup.OrderBy(c => c.PhysicalX).ThenBy(c => c.PhysicalY).ToList();
            var ranked2 = children2
                .Where(c => StableClassKeyOf(c.HumanReadableName ?? string.Empty) == classGroup.Key)
                .OrderBy(c => c.PhysicalX).ThenBy(c => c.PhysicalY).ToList();
            ranked2.Count.ShouldBe(ranked1.Count,
                $"every generation-1 {classGroup.Key} re-imports exactly once");
            pairs.AddRange(ranked1.Zip(ranked2));
        }
        pairs.Count.ShouldBe(7);
        return pairs;
    }

    /// <summary>
    /// Every paired component sits at its generation-1 position modulo ONE uniform
    /// shift (each import re-origins at its own layout top-left): all pairwise
    /// shifts must agree (no per-component drift), then each shifted position
    /// matches within tolerance.
    /// </summary>
    private static void AssertPositionsStable(
        IReadOnlyList<(Component Gen1, Component Gen2)> pairs, double positionToleranceUm)
    {
        var (anchor1, anchor2) = pairs.First(p =>
            StableClassKeyOf(p.Gen1.HumanReadableName ?? string.Empty) == "mmi2x2_dp");
        double dx = anchor1.PhysicalX - anchor2.PhysicalX;
        double dy = anchor1.PhysicalY - anchor2.PhysicalY;

        foreach (var (gen1, gen2) in pairs)
        {
            (gen2.PhysicalX + dx).ShouldBe(gen1.PhysicalX, positionToleranceUm,
                $"X of {gen1.HumanReadableName} is stable across generations (uniform shift removed)");
            (gen2.PhysicalY + dy).ShouldBe(gen1.PhysicalY, positionToleranceUm,
                $"Y of {gen1.HumanReadableName} is stable across generations (uniform shift removed)");
            gen2.RotationDegrees.ShouldBe(gen1.RotationDegrees, 1e-9);
        }
    }

    /// <summary>
    /// Re-keys a parsed topology into the generation-stable class namespace
    /// (<see cref="StableClassKeyOf"/> applied to the baked "class#rank/pin" keys).
    /// </summary>
    private static GdsHighestLevelRoundTripTests.NetlistTopology NormalizeTopology(
        GdsHighestLevelRoundTripTests.NetlistTopology topology) =>
        new(
            topology.InstanceCountsByClass
                .GroupBy(kv => StableClassKeyOf(kv.Key))
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value), StringComparer.Ordinal),
            topology.Edges.Select(NormalizeEdgeOrPort).ToHashSet(StringComparer.Ordinal),
            topology.Ports.Select(NormalizeEdgeOrPort).ToHashSet(StringComparer.Ordinal));

    /// <summary>Normalizes the class part of every endpoint of a baked edge or port key.</summary>
    private static string NormalizeEdgeOrPort(string key) =>
        string.Join(" = ", key.Split(" = ").Select(NormalizeEndpoint));

    private static string NormalizeEndpoint(string endpoint)
    {
        var hash = endpoint.IndexOf('#');
        return hash < 0 ? endpoint : StableClassKeyOf(endpoint[..hash]) + endpoint[hash..];
    }

    /// <summary>
    /// The generation-stable class key of a component reference or baked topology
    /// class: the round-trip folding (<see cref="GdsHighestLevelRoundTripTests.ClassKeyOf"/>),
    /// plus stripping the raw-code wrapper prefix (<c>component_</c>) each re-export
    /// adds around an imported draft's cell name — applied repeatedly, since every
    /// generation wraps the previous one.
    /// </summary>
    private static string StableClassKeyOf(string componentRef)
    {
        var key = GdsHighestLevelRoundTripTests.ClassKeyOf(componentRef);
        while (key.StartsWith("component_", StringComparison.Ordinal))
            key = GdsHighestLevelRoundTripTests.ClassKeyOf(key["component_".Length..]);
        return key;
    }
}
