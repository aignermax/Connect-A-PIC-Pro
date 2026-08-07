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
    /// straight (empty bbox) and recurses ONE level into the partial (through
    /// the wrapper — it is written UNFLATTENED, its device cells are real SREFs
    /// with port labels) → 29 route instances + the partial's 7 device
    /// instances of 5 distinct device cells.
    /// </summary>
    private const int RouteCellCount = 22;
    private const int DeviceCellCount = 5;
    private const int DeviceInstanceCount = 7;
    private const int PlacedInstanceCount = 36;

    /// <summary>The partial's device cells (the 7 instances: 2× mmi2x2_dp, 2× ebeam_crossing4).</summary>
    private static readonly string[] DeviceCellNames =
    [
        "mmi2x2_dp", "ebeam_bdc_te1550", "ebeam_crossing4",
        "ebeam_adiabatic_te1550", "ebeam_adiabatic_tm1550",
    ];

    /// <summary>
    /// Abutments that reconstruct: the 20 route↔route joints (unchanged) plus
    /// 10 route↔device joints — test.py routed to the ports gdsfactory read
    /// from the partial's (1,10) labels, so those route ends land nm-exact on
    /// the device label pins (1 nm grid, abutment tolerance 0.05 µm). The
    /// mmi2x2_dp joints still dangle: its demofab (501,1) labels sit 0.3 µm
    /// inside the cell edge (nazca's pin-text offset) while the route ends stop
    /// at the waveguide mouth — past the tolerance — and the remaining
    /// unconnected device ports are the circuit's free externals. Measured with
    /// <see cref="GdsAbutmentMatcher"/> on the committed file.
    /// </summary>
    private const int ReconstructedConnectionCount = 30;
    private const int RouteDeviceConnectionCount = 10;

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
    public async Task Explode_FirstImport_PlacesRouteCellsAndPartialDevices()
    {
        File.Exists(GdsPath).ShouldBeTrue($"Reference file missing: {GdsPath}");
        var sink = new LibrarySink(_prefsPath);
        var service = new GdsImportService(Store(), () => Array.Empty<ComponentTemplate>(), sink.Register);

        var outcome = await service.ImportAsync(GdsPath, TopCell, new GdsHierarchyImportOptions());

        outcome.Mode.ShouldBe(GdsHierarchyImportMode.ExplodeHierarchy);
        outcome.RegisteredComponents.Count.ShouldBe(RouteCellCount + DeviceCellCount);
        outcome.Instances.Count.ShouldBe(PlacedInstanceCount);

        // The partial's 7 devices come back (first import: every cell unknown,
        // so they register as drafts) at their absolute positions — the one-level
        // recursion into the unflattened partial, transforms composed through
        // the 'nazca' wrapper. Multiplicities: 2× mmi2x2_dp, 2× ebeam_crossing4.
        var deviceInstances = outcome.Instances
            .Where(i => DeviceCellNames.Contains(i.CellName)).ToList();
        deviceInstances.Count.ShouldBe(DeviceInstanceCount);
        deviceInstances.ShouldAllBe(i => i.CellDraftName == i.CellName && i.KnownComponentIdentifier == null);
        deviceInstances.Count(i => i.CellName == "mmi2x2_dp").ShouldBe(2);
        deviceInstances.Count(i => i.CellName == "ebeam_crossing4").ShouldBe(2);
        outcome.RegisteredComponents.Count(r => DeviceCellNames.Contains(r.CellDraftName))
            .ShouldBe(DeviceCellCount);

        // No failure cascade and no artifact-skip note: the partial is EXPANDED,
        // not skipped — the only remaining note is the zero-geometry drop of the
        // zero-length straight.
        outcome.Warnings.ShouldBeEmpty();
        var note = outcome.Infos.ShouldHaveSingleItem();
        note.ShouldContain("no geometry");
        note.ShouldContain("L0_N_a362bd09");
        note.ShouldContain("1 instance(s) skipped");

        // 30 abutments: 20 route↔route (unchanged) + 10 route↔device; none
        // involve top-cell ports (the file has no top-level port labels).
        outcome.Connections.Count.ShouldBe(ReconstructedConnectionCount);
        outcome.Connections.ShouldAllBe(c => !c.A.IsTopLevelPort && !c.B.IsTopLevelPort);
        var routeDevice = outcome.Connections
            .Where(c => IsDeviceEndpoint(outcome, c.A) != IsDeviceEndpoint(outcome, c.B))
            .ToList();
        routeDevice.Count.ShouldBe(RouteDeviceConnectionCount);

        // Spot-check two route↔device joints at their nm-exact positions: the
        // crossing's west/south ports and the bdc's opt1 take straight ends.
        AssertConnection(outcome,
            "straight_gdsfactorypcomponentspwaveguidespstraight_L12p_77ad6bb4#0", "heur_2",
            "ebeam_crossing4#0", "opt3", 311.875, 86.95);
        AssertConnection(outcome,
            "straight_gdsfactorypcomponentspwaveguidespstraight_L13p_45bcd56d#0", "heur_2",
            "ebeam_bdc_te1550#0", "opt1", 464.775, 82.15);

        // The mmi2x2_dp joints keep dangling (see ReconstructedConnectionCount).
        outcome.Connections.ShouldNotContain(c =>
            IsEndpointOf(outcome, c.A, "mmi2x2_dp") || IsEndpointOf(outcome, c.B, "mmi2x2_dp"));

        AssertHandComputedPositions(outcome);

        // Placement plan: every instance placeable (registered draft), group = top cell.
        var plan = GdsPlacementPlan.FromOutcome(outcome);
        plan.GroupName.ShouldBe(TopCell);
        plan.Placements.Count.ShouldBe(PlacedInstanceCount);
        plan.Placements.ShouldAllBe(p => p.ComponentIdentifier != null && p.Warning == null);
        plan.Connections.Count.ShouldBe(ReconstructedConnectionCount);

        // Execution: 36 components on the canvas, 30 frozen connections, one group.
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
        first.RegisteredComponents.Count.ShouldBe(RouteCellCount + DeviceCellCount);

        // Second import: every route cell AND every partial device cell resolves
        // to an existing component, so ZERO new drafts are registered — the
        // import must still produce all instance placements (no "nothing was
        // registered" misfire, no black-box fallback; those only apply to true
        // black-box mode).
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

    // ── Black-box: nested-label pins make the whole design importable ────────

    [Fact]
    public async Task BlackBox_MixedBackendFile_NestedLabelPins_RegistersAndPlaces()
    {
        // Black-box pin detection considers the FLATTENED top cell: the nested
        // device cells' port labels ((1,10)/(501,1), e.g. the mmi2x2_dp a0/a1/b0/b1)
        // become texts at their absolute positions after flattening, so the
        // whole design registers as ONE component with real pins instead of
        // failing "no pins" (the 15:05 user report).
        var sink = new LibrarySink(_prefsPath);
        var service = new GdsImportService(Store(), () => Array.Empty<ComponentTemplate>(), sink.Register);

        var outcome = await service.ImportAsync(
            GdsPath, TopCell, new GdsHierarchyImportOptions { Mode = GdsHierarchyImportMode.BlackBox });

        outcome.Mode.ShouldBe(GdsHierarchyImportMode.BlackBox);
        var registered = outcome.RegisteredComponents.ShouldHaveSingleItem();
        registered.ComponentName.ShouldBe(TopCell);

        var template = sink.Templates.ShouldHaveSingleItem(
            "the black-box draft registers as one library component");
        template.PinDefinitions.ShouldNotBeEmpty();
        template.PinDefinitions.Select(p => p.Name).ShouldContain(n => n.EndsWith("_a0"),
            "nested labels get instance-context prefixes, e.g. 'mmi2x2_dp#0_a0'");

        var plan = GdsPlacementPlan.FromOutcome(outcome);
        var canvas = new DesignCanvasViewModel();
        var executor = new GdsPlacementExecutor(
            canvas, new CommandManager(), () => sink.Templates.ToList());
        var report = await executor.ExecuteAsync(plan);
        report.PlacedCount.ShouldBe(1);
        report.SkippedPlacements.ShouldBeEmpty();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Spot-checks three route and three device instance positions hand-computed
    /// from test.py's <c>move</c> values and the partial's reference offsets.
    /// App space: origin at the top-left of the flattened top bbox (211.105,
    /// −342.7) in GDS coordinates (measured by <see cref="GdsCellFlattener"/>
    /// over all layers, the (68,0) devrec halo included), Y flipped (app y =
    /// −342.7 − placed-bbox-max-Y). The partial sits behind the 'nazca' wrapper
    /// at the origin, untransformed — the composed device offsets are the raw
    /// partial reference offsets.
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

        // ── Devices from the expanded partial ────────────────────────────────
        // mmi2x2_dp#0, ref offset (240.60, −488.80); cell bbox (0, −30)..(250, 30)
        // (the (1004,0) marker labels set the extent):
        // app x = 240.60 − 211.105 = 29.495; app y = −342.7 − (−458.80) = 116.1.
        var mmi = outcome.Instances.Single(i => i.InstanceName == "mmi2x2_dp#0");
        mmi.PositionXUm.ShouldBe(29.495, tol);
        mmi.PositionYUm.ShouldBe(116.1, tol);
        mmi.RotationDegrees.ShouldBe(0, tol);

        // ebeam_crossing4#0, ref offset (522.98, −424.85); our flattener's bbox
        // is (±5.1) — the crossing arms end at ±4.85 and the flattener adds half
        // the 0.5 µm path width:
        // app x = 522.98 − 5.1 − 211.105 = 306.775; app y = −342.7 − (−419.75) = 77.05.
        var crossing = outcome.Instances.Single(i => i.InstanceName == "ebeam_crossing4#0");
        crossing.PositionXUm.ShouldBe(306.775, tol);
        crossing.PositionYUm.ShouldBe(77.05, tol);

        // ebeam_adiabatic_te1550#0, ref offset (273.54, −426.35); bbox MinX −0.2
        // (taper tip path at 0.05 minus the half-width stroke), MaxY 3.0:
        // app x = 273.54 − 0.2 − 211.105 = 62.235; app y = −342.7 − (−423.35) = 80.65.
        var adiabatic = outcome.Instances.Single(i => i.InstanceName == "ebeam_adiabatic_te1550#0");
        adiabatic.PositionXUm.ShouldBe(62.235, tol);
        adiabatic.PositionYUm.ShouldBe(80.65, tol);
    }

    /// <summary>True when the endpoint is an instance pin of one of the partial's device cells.</summary>
    private static bool IsDeviceEndpoint(GdsImportOutcome outcome, GdsPinEndpoint endpoint) =>
        !endpoint.IsTopLevelPort && DeviceCellNames.Contains(outcome.Instances[endpoint.InstanceIndex].CellName);

    /// <summary>True when the endpoint is an instance pin of the named cell.</summary>
    private static bool IsEndpointOf(GdsImportOutcome outcome, GdsPinEndpoint endpoint, string cellName) =>
        !endpoint.IsTopLevelPort && outcome.Instances[endpoint.InstanceIndex].CellName == cellName;

    /// <summary>
    /// Asserts a reconstructed abutment between two instance pins at an
    /// nm-exact position (endpoint order is scan order — matched either way).
    /// </summary>
    private static void AssertConnection(
        GdsImportOutcome outcome,
        string instanceA, string pinA,
        string instanceB, string pinB,
        double expectedXUm, double expectedYUm)
    {
        const double tol = 1e-3;
        outcome.Connections.ShouldContain(c =>
            ((EndpointIs(outcome, c.A, instanceA, pinA) && EndpointIs(outcome, c.B, instanceB, pinB))
             || (EndpointIs(outcome, c.A, instanceB, pinB) && EndpointIs(outcome, c.B, instanceA, pinA)))
            && Math.Abs(c.XUm - expectedXUm) < tol
            && Math.Abs(c.YUm - expectedYUm) < tol);
    }

    /// <summary>True when the endpoint is the named pin of the named instance.</summary>
    private static bool EndpointIs(GdsImportOutcome outcome, GdsPinEndpoint endpoint, string instance, string pin) =>
        !endpoint.IsTopLevelPort
        && outcome.Instances[endpoint.InstanceIndex].InstanceName == instance
        && endpoint.PinName == pin;

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
