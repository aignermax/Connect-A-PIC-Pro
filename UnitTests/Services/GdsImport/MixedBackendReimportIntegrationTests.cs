using CAP.Avalonia.Commands;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components.Core;
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
/// <see cref="GdsDesignScopeTestHost"/> standing in for the open design's
/// component scope (same harness as <see cref="GdsImportServiceTests"/>).
/// </summary>
public class MixedBackendReimportIntegrationTests : IDisposable
{
    private static readonly string GdsPath = FindRepoRelative("Tools", "gds-test-data", "test.gds");

    private const string TopCell = "ConnectAPIC_Design";

    /// <summary>
    /// Counts of the committed file under route-cell dissolution: 31 direct
    /// references = 30 route cells + the 'nazca' wrapper around the merged
    /// partial. The route cells (gdsfactory straight/bend with (68,0) envelope)
    /// DISSOLVE into the top-cell route geometry — they register no drafts and
    /// place no instances; the zero-length straight drops (empty bbox) and the
    /// import recurses ONE level into the partial → the 7 device instances of
    /// 5 distinct device cells are the only placements.
    /// </summary>
    private const int DeviceCellCount = 5;
    private const int DeviceInstanceCount = 7;

    /// <summary>The partial's device cells (the 7 instances: 2× mmi2x2_dp, 2× ebeam_crossing4).</summary>
    private static readonly string[] DeviceCellNames =
    [
        "mmi2x2_dp", "ebeam_bdc_te1550", "ebeam_crossing4",
        "ebeam_adiabatic_te1550", "ebeam_adiabatic_tm1550",
    ];

    /// <summary>
    /// The dissolved route geometry reconstructs as LOGICAL links between the
    /// devices — one connection per fan-out chain, not one per segment joint:
    /// 2× mmi↔mmi, 4× mmi↔crossing, crossing↔crossing, crossing↔bdc,
    /// adiabatic↔crossing. Measured with the route matcher on the committed
    /// file; every chain bridges exactly two device pins, so nothing freezes.
    /// </summary>
    private const int ReconstructedConnectionCount = 9;

    private readonly GdsDesignScopeTestHost _host = new();

    public void Dispose() => _host.Dispose();

    // ── Explode: first import (every cell unknown) ───────────────────────────

    [Fact]
    public async Task Explode_FirstImport_DissolvesRouteCellsAndPlacesPartialDevices()
    {
        File.Exists(GdsPath).ShouldBeTrue($"Reference file missing: {GdsPath}");
        var service = _host.CreateService(() => Array.Empty<ComponentTemplate>());

        var outcome = await service.ImportAsync(GdsPath, TopCell, new GdsHierarchyImportOptions());

        outcome.Mode.ShouldBe(GdsHierarchyImportMode.ExplodeHierarchy);
        // Only the 5 device cells register — the envelope-carrying route cells
        // dissolve into the route geometry instead of becoming bogus drafts.
        outcome.RegisteredComponents.Count.ShouldBe(DeviceCellCount);
        outcome.Instances.Count.ShouldBe(DeviceInstanceCount);

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

        outcome.Warnings.ShouldBeEmpty();
        outcome.Infos.ShouldContain(i =>
            i.Contains("no geometry") && i.Contains("L0_N_a362bd09") && i.Contains("1 instance(s) skipped"));
        outcome.Infos.ShouldContain(i => i.Contains("dissolved"));

        // 9 logical links, all between device pins — the dissolved chains each
        // bridge exactly two of them; nothing is left as frozen route geometry.
        outcome.Connections.Count.ShouldBe(ReconstructedConnectionCount);
        outcome.Connections.ShouldAllBe(c =>
            IsDeviceEndpoint(outcome, c.A) && IsDeviceEndpoint(outcome, c.B));
        outcome.TopCellWaveguidePolygons.ShouldBeEmpty();

        // Spot-check three reconstructed links by their endpoint names.
        AssertLink(outcome, "mmi2x2_dp#1", "a1", "mmi2x2_dp#0", "a0");
        AssertLink(outcome, "mmi2x2_dp#0", "b1", "ebeam_crossing4#0", "opt3");
        AssertLink(outcome, "ebeam_crossing4#0", "opt4", "ebeam_bdc_te1550#0", "opt1");

        AssertHandComputedPositions(outcome);

        // Placement plan: every device placeable (registered draft), group = top cell.
        var plan = GdsPlacementPlan.FromOutcome(outcome);
        plan.GroupName.ShouldBe(TopCell);
        plan.Placements.Count.ShouldBe(DeviceInstanceCount);
        plan.Placements.ShouldAllBe(p => p.ComponentIdentifier != null && p.Warning == null);
        plan.Connections.Count.ShouldBe(ReconstructedConnectionCount);

        // Execution: 7 components on the canvas, 9 connections, one group.
        var canvas = new DesignCanvasViewModel();
        var executor = new GdsPlacementExecutor(
            canvas, new CommandManager(), () => _host.Templates.ToList());
        var report = await executor.ExecuteAsync(plan);

        report.PlacedCount.ShouldBe(DeviceInstanceCount);
        report.ConnectedCount.ShouldBe(ReconstructedConnectionCount);
        report.SkippedPlacements.ShouldBeEmpty();
        report.SkippedConnections.ShouldBeEmpty();
        report.GroupCreated.ShouldBeTrue();
        report.GroupName.ShouldBe(TopCell);

        var group = canvas.Components.ShouldHaveSingleItem().Component.ShouldBeOfType<ComponentGroup>();
        group.GroupName.ShouldBe(TopCell);
        group.ChildComponents.Count.ShouldBe(DeviceInstanceCount);
        group.InternalPaths.Count(p => p.StartPin is not null && p.EndPin is not null)
            .ShouldBe(ReconstructedConnectionCount);
    }

    // ── Explode: second import (every cell resolves to the library) ─────────

    [Fact]
    public async Task Explode_SecondImport_AllCellsKnown_StillPlacesInstances()
    {
        // First import populates the library (the device cells register as drafts).
        var first = await _host.CreateService(() => Array.Empty<ComponentTemplate>())
            .ImportAsync(GdsPath, TopCell, new GdsHierarchyImportOptions());
        first.RegisteredComponents.Count.ShouldBe(DeviceCellCount);

        // Second import: every device cell resolves to the existing library
        // (the route cells dissolve again — they never registered), so ZERO new
        // drafts are registered — the import must still produce all instance
        // placements (no "nothing was registered" misfire, no black-box
        // fallback; those only apply to true black-box mode).
        var second = await _host.CreateService()
            .ImportAsync(GdsPath, TopCell, new GdsHierarchyImportOptions());

        second.RegisteredComponents.ShouldBeEmpty("every device cell resolved to the existing library");
        second.Instances.Count.ShouldBe(DeviceInstanceCount);
        second.Instances.ShouldAllBe(i => i.KnownComponentIdentifier != null && i.CellDraftName == null);
        second.Warnings.ShouldBeEmpty();
        second.Connections.Count.ShouldBe(ReconstructedConnectionCount);

        var plan = GdsPlacementPlan.FromOutcome(second);
        plan.Placements.Count.ShouldBe(DeviceInstanceCount);
        plan.Placements.ShouldAllBe(p =>
            p.ComponentIdentifier != null && !p.IsImportedDraft && p.Warning == null);

        var canvas = new DesignCanvasViewModel();
        var executor = new GdsPlacementExecutor(
            canvas, new CommandManager(), () => _host.Templates.ToList());
        var report = await executor.ExecuteAsync(plan);
        report.PlacedCount.ShouldBe(DeviceInstanceCount);
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
        var service = _host.CreateService(() => Array.Empty<ComponentTemplate>());

        var outcome = await service.ImportAsync(
            GdsPath, TopCell, new GdsHierarchyImportOptions { Mode = GdsHierarchyImportMode.BlackBox });

        outcome.Mode.ShouldBe(GdsHierarchyImportMode.BlackBox);
        var registered = outcome.RegisteredComponents.ShouldHaveSingleItem();
        registered.ComponentName.ShouldBe(TopCell);

        var template = _host.Templates.ShouldHaveSingleItem(
            "the black-box draft registers as one library component");
        template.PinDefinitions.ShouldNotBeEmpty();
        template.PinDefinitions.Select(p => p.Name).ShouldContain(n => n.EndsWith("_a0"),
            "nested labels get instance-context prefixes, e.g. 'mmi2x2_dp#0_a0'");

        var plan = GdsPlacementPlan.FromOutcome(outcome);
        var canvas = new DesignCanvasViewModel();
        var executor = new GdsPlacementExecutor(
            canvas, new CommandManager(), () => _host.Templates.ToList());
        var report = await executor.ExecuteAsync(plan);
        report.PlacedCount.ShouldBe(1);
        report.SkippedPlacements.ShouldBeEmpty();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Spot-checks three device instance positions hand-computed from test.py's
    /// <c>move</c> values and the partial's reference offsets. App space: origin
    /// at the top-left of the flattened top bbox (211.105, −342.7) in GDS
    /// coordinates (measured by <see cref="GdsCellFlattener"/> over all layers,
    /// the (68,0) devrec halo included), Y flipped (app y = −342.7 −
    /// placed-bbox-max-Y). The partial sits behind the 'nazca' wrapper at the
    /// origin, untransformed — the composed device offsets are the raw partial
    /// reference offsets. (The route cells of the pre-dissolution era have no
    /// instances to spot-check anymore.)
    /// </summary>
    private static void AssertHandComputedPositions(GdsImportOutcome outcome)
    {
        const double tol = 1e-3;

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

    /// <summary>
    /// Asserts a reconstructed connection between the two named instance pins
    /// (endpoint order is scan order — matched either way).
    /// </summary>
    private static void AssertLink(
        GdsImportOutcome outcome,
        string instanceA, string pinA,
        string instanceB, string pinB)
    {
        outcome.Connections.ShouldContain(c =>
            (EndpointIs(outcome, c.A, instanceA, pinA) && EndpointIs(outcome, c.B, instanceB, pinB))
            || (EndpointIs(outcome, c.A, instanceB, pinB) && EndpointIs(outcome, c.B, instanceA, pinA)),
            $"expected a reconstructed link {instanceA}.{pinA} ↔ {instanceB}.{pinB}");
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
