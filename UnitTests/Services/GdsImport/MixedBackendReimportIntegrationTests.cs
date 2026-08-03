using System.Collections.ObjectModel;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components.Core;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using CAP_DataAccess.Import.Gds;
using Shouldly;
using Xunit;

namespace UnitTests.Services.GdsImport;

/// <summary>
/// End-to-end fit-check against <c>Tools/gds-test-data/test.gds</c> — a real
/// Lunima mixed-backend export part 2 (gdsfactory merging
/// <c>test_nazca_partial.gds</c>), the exact file whose re-import the user
/// reported broken. See <c>Tools/gds-test-data/README.md</c> for the file's
/// origin and structure. Runs the full stack: <see cref="GdsImportService"/> →
/// <see cref="GdsPlacementPlan"/> → <see cref="GdsPlacementExecutor"/> with a
/// temp-root <see cref="UserPdkStore"/> and the real
/// <see cref="CustomComponentLibraryRegistrar"/> (same harness as
/// <see cref="GdsImportServiceTests"/>).
/// </summary>
public class MixedBackendReimportIntegrationTests : IDisposable
{
    private static readonly string GdsPath = FindRepoRelative("Tools", "gds-test-data", "test.gds");

    private const string TopCell = "ConnectAPIC_Design";

    /// <summary>
    /// Instance counts of the committed file, verified against the generator
    /// script (test.py, 30 route segments + 1 merged partial) and re-measured
    /// with our own reader: 31 direct references = 30 route cells + the
    /// 'nazca' wrapper around the merged partial. Explode drops the zero-length
    /// straight (empty bbox) and the artifact wrapper → 29 placed instances of
    /// 22 distinct route cells (20 straights + 2 bend_circular variants).
    /// </summary>
    private const int RouteCellCount = 22;
    private const int PlacedInstanceCount = 29;

    /// <summary>
    /// Route↔route abutments that reconstruct. test.py's chains run THROUGH the
    /// skipped partial's devices (mmi2x2_dp, crossings, bdc, adiabatics), so
    /// the joints at device ports dangle; the 20 remaining direct joints are
    /// nm-exact (1 nm grid, abutment tolerance 0.05 µm). Measured with
    /// <see cref="GdsAbutmentMatcher"/> on the committed file.
    /// </summary>
    private const int ReconstructedConnectionCount = 20;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lunima-gdsreimport-" + Guid.NewGuid().ToString("N"));
    private readonly string _prefsPath =
        Path.Combine(Path.GetTempPath(), $"lunima-gdsreimport-prefs-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        if (File.Exists(_prefsPath)) File.Delete(_prefsPath);
    }

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

    private UserPdkStore Store() => new(
        Path.Combine(_root, "user-pdks"), new PdkJsonSaver(), new PdkLoader());

    // ── Explode: first import (every cell unknown) ───────────────────────────

    [Fact]
    public async Task Explode_FirstImport_PlacesRouteCells_SkipsPartialAndZeroLength()
    {
        File.Exists(GdsPath).ShouldBeTrue($"Reference file missing: {GdsPath}");
        var sink = new LibrarySink(_prefsPath);
        var service = new GdsImportService(Store(), () => Array.Empty<ComponentTemplate>(), sink.Register);

        var outcome = await service.ImportAsync(GdsPath, TopCell, new GdsHierarchyImportOptions());

        outcome.Mode.ShouldBe(GdsHierarchyImportMode.ExplodeHierarchy);
        outcome.RegisteredComponents.Count.ShouldBe(RouteCellCount);
        outcome.Instances.Count.ShouldBe(PlacedInstanceCount);

        // No failure cascade: the flattened partial is skipped by convention and
        // the zero-length straight by the zero-geometry drop — one info note each.
        outcome.Warnings.ShouldBeEmpty();
        outcome.Infos.Count.ShouldBe(2);
        outcome.Infos.ShouldContain(i =>
            i.Contains("export artifact") && i.Contains("'nazca'") && i.Contains("ConnectAPIC_NazcaPartial"));
        outcome.Infos.ShouldContain(i =>
            i.Contains("no geometry") && i.Contains("L0_N_a362bd09") && i.Contains("1 instance(s) skipped"));

        // 20 route↔route abutments; none involve top-cell ports (the file has
        // no top-level port labels).
        outcome.Connections.Count.ShouldBe(ReconstructedConnectionCount);
        outcome.Connections.ShouldAllBe(c => !c.A.IsTopLevelPort && !c.B.IsTopLevelPort);

        AssertHandComputedPositions(outcome);

        // Placement plan: every instance placeable (registered draft), group = top cell.
        var plan = GdsPlacementPlan.FromOutcome(outcome);
        plan.GroupName.ShouldBe(TopCell);
        plan.Placements.Count.ShouldBe(PlacedInstanceCount);
        plan.Placements.ShouldAllBe(p => p.ComponentIdentifier != null && p.Warning == null);
        plan.Connections.Count.ShouldBe(ReconstructedConnectionCount);

        // Execution: 29 components on the canvas, 20 frozen connections, one group.
        var canvas = new DesignCanvasViewModel();
        var executor = new GdsPlacementExecutor(
            canvas, new CommandManager(), () => sink.Templates.ToList());
        var report = await executor.ExecuteAsync(plan);

        report.PlacedCount.ShouldBe(PlacedInstanceCount);
        report.ConnectedCount.ShouldBe(ReconstructedConnectionCount);
        report.SkippedPlacements.ShouldBeEmpty();
        report.SkippedConnections.ShouldBeEmpty();
        report.GroupCreated.ShouldBeTrue();
        report.GroupName.ShouldBe(TopCell);

        var group = canvas.Components.ShouldHaveSingleItem().Component.ShouldBeOfType<ComponentGroup>();
        group.GroupName.ShouldBe(TopCell);
        group.ChildComponents.Count.ShouldBe(PlacedInstanceCount);
        group.InternalPaths.Count.ShouldBe(ReconstructedConnectionCount);
    }

    // ── Explode: second import (every cell resolves to the library) ─────────

    [Fact]
    public async Task Explode_SecondImport_AllCellsKnown_StillPlacesInstances()
    {
        // First import populates the library (the 13:26 user run).
        var sink = new LibrarySink(_prefsPath);
        var store = Store();
        var first = await new GdsImportService(store, () => Array.Empty<ComponentTemplate>(), sink.Register)
            .ImportAsync(GdsPath, TopCell, new GdsHierarchyImportOptions());
        first.RegisteredComponents.Count.ShouldBe(RouteCellCount);

        // Second import: every route cell resolves to an existing component, so
        // ZERO new drafts are registered — the import must still produce all
        // instance placements (no "nothing was registered" misfire, no
        // black-box fallback; those only apply to true black-box mode).
        var second = await new GdsImportService(store, () => sink.Templates.ToList(), sink.Register)
            .ImportAsync(GdsPath, TopCell, new GdsHierarchyImportOptions());

        second.RegisteredComponents.ShouldBeEmpty("every cell resolved to the existing library");
        second.Instances.Count.ShouldBe(PlacedInstanceCount);
        second.Instances.ShouldAllBe(i => i.KnownComponentIdentifier != null && i.CellDraftName == null);
        second.Warnings.ShouldBeEmpty();
        second.Connections.Count.ShouldBe(ReconstructedConnectionCount);

        var plan = GdsPlacementPlan.FromOutcome(second);
        plan.Placements.Count.ShouldBe(PlacedInstanceCount);
        plan.Placements.ShouldAllBe(p =>
            p.ComponentIdentifier != null && !p.IsImportedDraft && p.Warning == null);

        var canvas = new DesignCanvasViewModel();
        var executor = new GdsPlacementExecutor(
            canvas, new CommandManager(), () => sink.Templates.ToList());
        var report = await executor.ExecuteAsync(plan);
        report.PlacedCount.ShouldBe(PlacedInstanceCount);
        report.ConnectedCount.ShouldBe(ReconstructedConnectionCount);
        report.GroupCreated.ShouldBeTrue();
    }

    // ── Black-box: current honest behavior on this file ──────────────────────

    [Fact]
    public async Task BlackBox_MixedBackendFile_NoPins_NothingRegistered_CurrentBehavior()
    {
        // Pins the CURRENT behavior the user hit at 15:05: a black-box import of
        // this file finds no pins. The top cell carries no OWN port labels
        // (gdsfactory writes none for a port-less circuit; Lunima's mixed-backend
        // export writes no top-level labels either), and the waveguide-edge
        // heuristic finds no (1,0) polygon exactly touching the flattened top
        // bbox — every route end sits 0.225 µm inside the (68,0) devrec halo of
        // the outermost cell. The pin-less draft is unpersistable, so nothing is
        // registered and the single black-box placement is skipped with clear
        // warnings.
        //
        // FOLLOW-UP: black-box pin detection should also consider NESTED port
        // labels (the absorbed sub-cells carry (1,10) and (501,1) labels, e.g.
        // the mmi2x2_dp pins a0/a1/b0/b1) — the draft would then register with
        // real pins instead of failing "no pins".
        var sink = new LibrarySink(_prefsPath);
        var service = new GdsImportService(Store(), () => Array.Empty<ComponentTemplate>(), sink.Register);

        var outcome = await service.ImportAsync(
            GdsPath, TopCell, new GdsHierarchyImportOptions { Mode = GdsHierarchyImportMode.BlackBox });

        outcome.Mode.ShouldBe(GdsHierarchyImportMode.BlackBox);
        outcome.RegisteredComponents.ShouldBeEmpty();
        outcome.Warnings.ShouldContain(w =>
            w.Contains($"'{TopCell}' was not registered: no pins detected"));
        outcome.Warnings.ShouldContain(w =>
            w.Contains("No importable component drafts remained"));

        var plan = GdsPlacementPlan.FromOutcome(outcome);
        var placement = plan.Placements.ShouldHaveSingleItem();
        placement.ComponentIdentifier.ShouldBeNull();
        placement.Warning.ShouldNotBeNull().ShouldContain("black-box component cannot be placed");

        var canvas = new DesignCanvasViewModel();
        var executor = new GdsPlacementExecutor(
            canvas, new CommandManager(), () => sink.Templates.ToList());
        var report = await executor.ExecuteAsync(plan);
        report.PlacedCount.ShouldBe(0);
        report.SkippedPlacements.ShouldHaveSingleItem().ShouldContain(TopCell);
        report.GroupCreated.ShouldBeFalse();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Spot-checks three instance positions hand-computed from test.py's
    /// <c>move</c> values. App space: origin at the top-left of the flattened
    /// top bbox (211.105, −342.7) in GDS coordinates (measured by
    /// <see cref="GdsCellFlattener"/> over all layers, the (68,0) devrec halo
    /// included), Y flipped (app y = −342.7 − placed-bbox-max-Y).
    /// </summary>
    private static void AssertHandComputedPositions(GdsImportOutcome outcome)
    {
        const double tol = 1e-3;

        // straight(L=13.80), move (482.00, −424.85), rot 0; cell bbox y −0.725..0.725:
        // app x = 482.00 − 211.105 = 270.895; app y = −342.7 − (−424.125) = 81.425.
        var straight = outcome.Instances.Single(
            i => i.InstanceName == "straight_gdsfactorypcomponentspwaveguidespstraight_L13p_2d84c128#0");
        straight.PositionXUm.ShouldBe(270.895, tol);
        straight.PositionYUm.ShouldBe(81.425, tol);
        straight.RotationDegrees.ShouldBe(0, tol);

        // bend_circular 5be4f23f, 3rd occurrence, move (512.98, −492.80), rot 0;
        // cell bbox (0, −0.725)..(10.725, 10):
        // app x = 512.98 − 211.105 = 301.875; app y = −342.7 − (−482.80) = 140.1.
        var bend = outcome.Instances.Single(
            i => i.InstanceName == "bend_circular_gdsfactorypcomponentspbendspbend_circular_5be4f23f#2");
        bend.PositionXUm.ShouldBe(301.875, tol);
        bend.PositionYUm.ShouldBe(140.1, tol);
        bend.RotationDegrees.ShouldBe(0, tol);

        // straight(L=88.10), move (224.80, −386.70), GDS rot 270° (app 90°);
        // rotated cell bbox (−0.725, −88.1)..(0.725, 0):
        // app x = 224.075 − 211.105 = 12.97; app y = −342.7 − (−386.70) = 44.0.
        var rotated = outcome.Instances.Single(
            i => i.InstanceName == "straight_gdsfactorypcomponentspwaveguidespstraight_L88p_6d1f94ef#0");
        rotated.PositionXUm.ShouldBe(12.97, tol);
        rotated.PositionYUm.ShouldBe(44.0, tol);
        rotated.RotationDegrees.ShouldBe(90, tol);
    }

    private static string FindRepoRelative(params string[] segments)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Tools", "gds-test-data")))
        {
            dir = dir.Parent;
        }
        if (dir == null) throw new InvalidOperationException("Could not locate repository root");
        return Path.Combine(new[] { dir.FullName }.Concat(segments).ToArray());
    }
}
