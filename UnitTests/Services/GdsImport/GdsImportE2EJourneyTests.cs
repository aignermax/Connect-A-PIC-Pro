using System.Numerics;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Core;
using CAP_DataAccess.Import.Gds;
using Shouldly;
using Xunit;

namespace UnitTests.Services.GdsImport;

/// <summary>
/// E2E journey (issue #904): export a known SiEPIC design (deliberately
/// unconnected + one non-cardinal rotation) to GDS with real nazca → hierarchy-
/// explode import with auto-connect ON → assert pin/pair/unroutable census →
/// headless S-matrix simulation (light must reach both outputs) → .lun
/// save/load (poses, rotation, frozen paths survive) → re-export and
/// third-generation import (topology + positions stable). Each numbered step
/// is its own labelled assertion block so a failure names the broken seam.
/// </summary>
[Trait("Category", "Slow")]
public class GdsImportE2EJourneyTests : IDisposable
{
    /// <summary>
    /// Expected arriving amplitude at each output coupler is ≈0.056 (the GC's
    /// port2→port2 magnitude 0.081 × the Y-branch arm's 0.69); anything below
    /// this floor means the light path through the auto-connected wires broke.
    /// </summary>
    private const double NoiseFloorAmplitude = 0.01;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lunima-gds-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly List<GdsDesignScopeTestHost> _hosts = new();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        foreach (var host in _hosts) host.Dispose();
    }

    [SkippableFact]
    public async Task FullJourney_Export_ImportAutoConnect_Simulate_SaveLoad_Reexport()
    {
        var python = await GdsUserDesignFixture.FindNazcaPythonAsync();
        Skip.If(python == null, "No Python with nazca available — the journey needs the real engine.");

        // ── Step 1: build + export the original design (real nazca) ──
        var bundled = GdsE2EJourneyHarness.ScenarioTemplates();
        bundled.Count.ShouldBe(3, "step 1: the SiEPIC PDK carries the three scenario templates");
        var originalCanvas = GdsE2EJourneyHarness.BuildOriginalDesign(bundled);
        originalCanvas.Components.Count.ShouldBe(5, "step 1: five placements");
        originalCanvas.Connections.ShouldBeEmpty("step 1: the design ships UNWIRED — auto-connect must do all wiring");
        AssertOriginalJointGeometry(originalCanvas);
        var gds1 = await GdsE2EJourneyHarness.ExportAsync(python!, _root, "gen1", originalCanvas, bundled);

        // ── Step 2: hierarchy-explode import with auto-connect ALL pins ON ──
        var analysis = await GdsImportService.AnalyzeAsync(gds1);
        analysis.TopCellCandidates.ShouldBe(new[] { "ConnectAPIC_Design" }, "step 2: our export's top cell");
        var (canvas1, report1, host1) = await ImportAndPlaceAsync(gds1, bundled, autoConnectAllPins: true);
        report1.PlacedCount.ShouldBe(5, "step 2: every instance resolves to a bundled template and places");
        report1.GroupCreated.ShouldBeTrue("step 2: the import groups as the top cell");
        var group1 = canvas1.Components.ShouldHaveSingleItem().Component.ShouldBeOfType<ComponentGroup>();
        var children1 = group1.GetAllComponentsRecursive().ToList();
        children1.Count.ShouldBe(5);
        children1.Count(c => c.HumanReadableName == GdsE2EJourneyHarness.GratingCoupler).ShouldBe(3);
        children1.Count(c => c.HumanReadableName == GdsE2EJourneyHarness.Splitter).ShouldBe(1);
        children1.Count(c => c.HumanReadableName == GdsE2EJourneyHarness.OutlierDc).ShouldBe(1);

        // ── Step 3: pin + auto-connect census (3 pairs routed, 0 unroutable) ──
        children1.SelectMany(c => c.PhysicalPins).Count().ShouldBe(10,
            "step 3: pin census — 3 couplers × 1 + Y-branch × 3 + Broadband DC × 4");
        report1.AutoConnectedCount.ShouldBe(3,
            "step 3: coupler→Y-branch plus the two Y-arm→coupler pairs auto-connect");
        report1.AutoConnectFailedCount.ShouldBe(0, "step 3: zero unroutable pairs");
        report1.AutoConnectUnpairedPinCount.ShouldBe(4,
            "step 3: the far-away Broadband DC's four pins stay unpaired");
        var pinned1 = group1.InternalPaths.Where(p => p.StartPin is not null).ToList();
        pinned1.Count.ShouldBe(3, "step 3: the auto-connected routes freeze into the group");
        var outlier1 = children1.Single(c => c.HumanReadableName == GdsE2EJourneyHarness.OutlierDc);
        (outlier1.RotationDegrees % 90.0).ShouldNotBe(0.0,
            "step 3: the Broadband DC keeps a NON-cardinal rotation through export + import");

        // ── Step 4: headless S-matrix simulation — light reaches both outputs ──
        MarkOutputCouplersListenOnly(children1);
        var simulation = await new SimulationService().RunAsync(canvas1);
        simulation.Success.ShouldBeTrue($"step 4: simulation must run — {simulation.ErrorMessage}");
        simulation.LightSourceCount.ShouldBe(1,
            "step 4: only the input coupler injects; the two output couplers listen");
        foreach (var outputPin in OutputCouplerPins(children1))
        {
            ArrivingAmplitudeAt(simulation, outputPin).ShouldBeGreaterThan(NoiseFloorAmplitude,
                "step 4: light must arrive above the noise floor at each output coupler");
        }

        // ── Step 5: .lun save/load — poses, rotation and frozen paths survive ──
        foreach (var template in bundled) host1.Templates.Add(template);
        var savePath = Path.Combine(_root, "journey.lun");
        await GdsE2EJourneyHarness.SaveToFile(
            GdsE2EJourneyHarness.CreateFileOperations(canvas1, host1), savePath);
        var loadHost = NewHost();
        foreach (var template in bundled) loadHost.Templates.Add(template);
        var loadedCanvas = new DesignCanvasViewModel();
        await GdsE2EJourneyHarness.LoadFromFile(
            GdsE2EJourneyHarness.CreateFileOperations(loadedCanvas, loadHost), savePath);

        var group2 = loadedCanvas.Components.ShouldHaveSingleItem().Component.ShouldBeOfType<ComponentGroup>();
        var children2 = group2.GetAllComponentsRecursive().ToList();
        children2.Count.ShouldBe(5, "step 5: all five components reload");
        var outlier2 = children2.Single(c => c.HumanReadableName == GdsE2EJourneyHarness.OutlierDc);
        outlier2.RotationDegrees.ShouldBe(outlier1.RotationDegrees, 0.5,
            "step 5: the non-cardinal rotation survives the .lun round trip");
        AssertPinPositionsMatch(children1, children2);
        group2.InternalPaths.Count(p => p.StartPin is not null).ShouldBe(3,
            "step 5: the three frozen auto-connect routes survive save/load");
        MarkOutputCouplersListenOnly(children2);
        var simulation2 = await new SimulationService().RunAsync(loadedCanvas);
        simulation2.Success.ShouldBeTrue($"step 5: the reloaded design still simulates — {simulation2.ErrorMessage}");
        foreach (var outputPin in OutputCouplerPins(children2))
        {
            ArrivingAmplitudeAt(simulation2, outputPin).ShouldBeGreaterThan(NoiseFloorAmplitude,
                "step 5: the reloaded design still lights up both outputs");
        }

        // ── Step 6: re-export + third-generation import — topology invariants hold ──
        var gds2 = await GdsE2EJourneyHarness.ExportAsync(
            python!, _root, "gen2", loadedCanvas, loadHost.Templates.ToList());
        var (canvas3, report3, _) = await ImportAndPlaceAsync(gds2, bundled, autoConnectAllPins: true);
        report3.PlacedCount.ShouldBe(5, "step 6: the re-export re-imports completely");
        // Route-derived reconnection now succeeds at coupler pins (#909): the
        // export stamps a top-cell port label exactly on each coupler pin, and
        // the matcher drops a port touch coincident with an instance pin from
        // the pairing decision — the joint is 2-pin again, so all three drawn
        // routes reconnect directly and nothing is left frozen or for
        // auto-connect to re-derive.
        report3.ConnectedCount.ShouldBe(3,
            "step 6: the three coupler-terminated routes reconnect route-derived (#909 coincident-port rule)");
        report3.FrozenRoutePathCount.ShouldBe(0,
            "step 6: every route polygon became a real connection — none stay frozen");
        report3.AutoConnectedCount.ShouldBe(0,
            "step 6: route derivation already restored all three wires — auto-connect has nothing left");
        report3.AutoConnectFailedCount.ShouldBe(0, "step 6: zero unroutable pairs in generation 3");
        report3.AutoConnectUnpairedPinCount.ShouldBe(4,
            "step 6: the Broadband DC's four pins stay unpaired in generation 3 too");
        var group3 = canvas3.Components.ShouldHaveSingleItem().Component.ShouldBeOfType<ComponentGroup>();
        var children3 = group3.GetAllComponentsRecursive().ToList();
        AssertSameTopology(canvas1, canvas3);
        AssertCircuitPositionsStable(children1, children3);
    }

    // ── Step harnesses ───────────────────────────────────────────────────

    /// <summary>Sanity-pins the facing joints the auto-connect stage must later find.</summary>
    private static void AssertOriginalJointGeometry(DesignCanvasViewModel canvas)
    {
        var components = canvas.Components.Select(vm => vm.Component).ToList();
        var splitter = components.Single(c => c.HumanReadableName == GdsE2EJourneyHarness.Splitter);
        var couplers = components.Where(c => c.HumanReadableName == GdsE2EJourneyHarness.GratingCoupler)
            .OrderBy(c => c.PhysicalX).ThenBy(c => c.PhysicalY).ToList();
        AssertPinAt(GdsE2EJourneyHarness.Pin(couplers[0], "port 2"), 139.969, 113.669);
        AssertPinAt(GdsE2EJourneyHarness.Pin(splitter, "port 1"), 200.1, 113.669);
        AssertPinAt(GdsE2EJourneyHarness.Pin(couplers[1], "port 2"), 300.05, 103.5);
        AssertPinAt(GdsE2EJourneyHarness.Pin(couplers[2], "port 2"), 300.05, 143.5);
    }

    private static void AssertPinAt(PhysicalPin pin, double x, double y)
    {
        var (px, py) = pin.GetAbsolutePosition();
        px.ShouldBe(x, 0.1, $"step 1: pin '{pin.Name}' X");
        py.ShouldBe(y, 0.1, $"step 1: pin '{pin.Name}' Y");
    }

    /// <summary>Explode-import + placement through the same path the GDS-import dialog uses.</summary>
    private async Task<(DesignCanvasViewModel Canvas, GdsPlacementReport Report, GdsDesignScopeTestHost Host)>
        ImportAndPlaceAsync(string gdsPath, IReadOnlyList<CAP.Avalonia.ViewModels.Library.ComponentTemplate> bundled,
            bool autoConnectAllPins)
    {
        var host = NewHost();
        var service = host.CreateService(() => bundled.Concat(host.Templates).ToList());
        var options = new GdsHierarchyImportOptions
        {
            PinDetection = new GdsPinDetectionOptions { PortLayers = [(1, 10), (501, 1)] },
        };
        var outcome = await service.ImportAsync(gdsPath, "ConnectAPIC_Design", options, null);

        var canvas = new DesignCanvasViewModel();
        canvas.InitializeAStarRouting(-100, -100, 1100, 900);
        var report = await new GdsPlacementExecutor(canvas, null, () => bundled.Concat(host.Templates).ToList())
            .ExecuteAsync(GdsPlacementPlan.FromOutcome(outcome), autoConnectAllPins: autoConnectAllPins);
        return (canvas, report, host);
    }

    private GdsDesignScopeTestHost NewHost()
    {
        var host = new GdsDesignScopeTestHost();
        _hosts.Add(host);
        return host;
    }

    /// <summary>
    /// Switches the two output couplers to listen-only (#690) so the only
    /// injected light is the input coupler's — anything arriving at the outputs
    /// then proves propagation through the auto-connected wires.
    /// </summary>
    private static void MarkOutputCouplersListenOnly(IReadOnlyList<Component> children)
    {
        foreach (var coupler in OutputCouplers(children))
            coupler.LaserEnabled = false;
    }

    /// <summary>The two rightmost grating couplers — the circuit's outputs.</summary>
    private static List<Component> OutputCouplers(IReadOnlyList<Component> children) =>
        children.Where(c => c.HumanReadableName == GdsE2EJourneyHarness.GratingCoupler)
            .OrderByDescending(c => c.PhysicalX).Take(2).ToList();

    private static IEnumerable<PhysicalPin> OutputCouplerPins(IReadOnlyList<Component> children) =>
        OutputCouplers(children).Select(c => GdsE2EJourneyHarness.Pin(c, "port 2"));

    /// <summary>
    /// The simulated amplitude ARRIVING at <paramref name="internalPin"/> (its
    /// in-flow). The group's external pins share their internal pin's LogicalPin,
    /// so the internal pin's flow is exactly the flow the field map carries.
    /// </summary>
    private static double ArrivingAmplitudeAt(SimulationResult simulation, PhysicalPin internalPin)
    {
        var fields = simulation.FieldResults.ShouldNotBeNull();
        var flowId = internalPin.LogicalPin.ShouldNotBeNull().IDInFlow;
        return fields.TryGetValue(flowId, out var amplitude) ? Complex.Abs(amplitude) : 0.0;
    }

    /// <summary>Every pin sits exactly where it was before the save (sorted census).</summary>
    private static void AssertPinPositionsMatch(
        IReadOnlyList<Component> saved, IReadOnlyList<Component> loaded)
    {
        var savedPositions = SortedPinPositions(saved);
        var loadedPositions = SortedPinPositions(loaded);
        loadedPositions.Count.ShouldBe(savedPositions.Count, "step 5: pin census is stable");
        foreach (var (loadedPin, savedPin) in loadedPositions.Zip(savedPositions))
        {
            loadedPin.x.ShouldBe(savedPin.x, 0.01, "step 5: pin X must not drift across save/load");
            loadedPin.y.ShouldBe(savedPin.y, 0.01, "step 5: pin Y must not drift across save/load");
        }
    }

    private static List<(double x, double y)> SortedPinPositions(IReadOnlyList<Component> components) =>
        components.SelectMany(c => c.PhysicalPins.Select(p => p.GetAbsolutePosition()))
            .OrderBy(p => p.x).ThenBy(p => p.y).ToList();

    /// <summary>Generation 3 derives the same netlist topology as generation 1.</summary>
    private static void AssertSameTopology(DesignCanvasViewModel canvas1, DesignCanvasViewModel canvas3)
    {
        var topology1 = GdsHighestLevelRoundTripTests.ParseTopology(
            GdsHighestLevelRoundTripTests.DeriveYaml(canvas1, "gen1"), mapOriginalPinNames: false);
        var topology3 = GdsHighestLevelRoundTripTests.ParseTopology(
            GdsHighestLevelRoundTripTests.DeriveYaml(canvas3, "gen3"), mapOriginalPinNames: false);
        topology3.InstanceCountsByClass.ShouldBe(topology1.InstanceCountsByClass,
            "step 6: same instance census in both generations");
        topology3.Edges.ShouldBe(topology1.Edges, ignoreOrder: true,
            customMessage: "step 6: the re-export restores the same netlist edges — none doubled, none lost");
        topology3.Ports.ShouldBe(topology1.Ports, ignoreOrder: true,
            customMessage: "step 6: the free-pin census round-trips");
    }

    /// <summary>
    /// The wired circuit (couplers + Y-branch) sits at its generation-1 positions
    /// modulo ONE uniform re-origin shift. The unconnected Broadband DC is checked
    /// for presence only: its non-cardinal pose is covered by the .lun assertions
    /// in step 5.
    /// </summary>
    private static void AssertCircuitPositionsStable(
        IReadOnlyList<Component> children1, IReadOnlyList<Component> children3)
    {
        var circuit1 = WiredCircuitByPosition(children1);
        var circuit3 = WiredCircuitByPosition(children3);
        circuit3.Count.ShouldBe(circuit1.Count);
        var dx = circuit1[0].PhysicalX - circuit3[0].PhysicalX;
        var dy = circuit1[0].PhysicalY - circuit3[0].PhysicalY;
        foreach (var (gen1, gen3) in circuit1.Zip(circuit3))
        {
            (gen3.PhysicalX + dx).ShouldBe(gen1.PhysicalX, 1.0,
                $"step 6: X of {gen1.HumanReadableName} is stable (uniform shift removed)");
            (gen3.PhysicalY + dy).ShouldBe(gen1.PhysicalY, 1.0,
                $"step 6: Y of {gen1.HumanReadableName} is stable (uniform shift removed)");
        }
        children3.Count(c => c.HumanReadableName == GdsE2EJourneyHarness.OutlierDc)
            .ShouldBe(1, "step 6: the Broadband DC re-imports too");
    }

    private static List<Component> WiredCircuitByPosition(IReadOnlyList<Component> children) =>
        children.Where(c => c.HumanReadableName != GdsE2EJourneyHarness.OutlierDc)
            .OrderBy(c => c.PhysicalX).ThenBy(c => c.PhysicalY).ToList();
}
