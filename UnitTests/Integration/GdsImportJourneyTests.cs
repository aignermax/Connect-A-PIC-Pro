using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Core;
using Shouldly;
using UnitTests.Services.GdsImport;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// End-to-end journey test for GDS import (issue #1001, rung 1 / #537):
/// import a GDS with port labels, place the cell on a canvas next to a
/// bundled demo component, route a connection, simulate light propagation,
/// save/load the design, and re-export — proving the imported cell works
/// as a first-class citizen through the whole product.
/// <para>
/// Each step is a separate test over a shared <see cref="IClassFixture{T}"/>
/// so a failure names the broken step. Steps 1–4 run headless; step 5 is
/// nazca-gated with <c>Skip.If</c>.
/// </para>
/// </summary>
public class GdsImportJourneyTests : IClassFixture<GdsImportJourneyFixture>
{
    private readonly GdsImportJourneyFixture _f;

    public GdsImportJourneyTests(GdsImportJourneyFixture fixture) => _f = fixture;

    /// <summary>
    /// Step 1 — Import: the GDS cell arrives as a black-box component with
    /// the expected detected pins (count, names, positions).
    /// </summary>
    [Fact]
    public void Step1_Import_CellArrivesWithDetectedPins()
    {
        _f.Outcome.Warnings.ShouldBeEmpty();
        _f.Outcome.RegisteredComponents.Count.ShouldBe(1);
        _f.Outcome.RegisteredComponents[0].CellDraftName.ShouldBe("wg");

        var template = _f.Host.Templates.Single(t =>
            t.Name == "wg" && t.PdkSource == _f.Outcome.UserPdkName);
        template.PinDefinitions.Length.ShouldBe(2);
        template.PinDefinitions.Select(p => p.Name).ShouldBe(
            new[] { "in", "out" }, ignoreOrder: true);

        // Pin positions: "in" at left edge (0, 2 µm), "out" at right edge (10, 2 µm).
        var inPin = template.PinDefinitions.Single(p => p.Name == "in");
        inPin.OffsetX.ShouldBe(0, 1e-9);
        inPin.OffsetY.ShouldBe(2, 1e-9);
        var outPin = template.PinDefinitions.Single(p => p.Name == "out");
        outPin.OffsetX.ShouldBe(10, 1e-9);
        outPin.OffsetY.ShouldBe(2, 1e-9);
    }

    /// <summary>
    /// Step 2 — Place + route: the imported component and a bundled Grating
    /// Coupler sit on the canvas; their connection exists and has a
    /// geometric path.
    /// </summary>
    [Fact]
    public void Step2_PlaceAndRoute_ConnectionExistsWithGeometricPath()
    {
        _f.PlaceReport.PlacedCount.ShouldBe(1);
        _f.Canvas.Components.Count.ShouldBe(2,
            "the imported waveguide plus the Grating Coupler");

        _f.Canvas.Connections.Count.ShouldBe(1);
        var connVm = _f.Canvas.Connections[0];
        connVm.Connection.StartPin.ShouldNotBeNull();
        connVm.Connection.EndPin.ShouldNotBeNull();

        // The connection has a routed geometric path.
        connVm.Connection.RoutedPath.ShouldNotBeNull();
        connVm.Connection.RoutedPath!.Segments.Count.ShouldBeGreaterThan(0,
            "the routed connection must have at least one segment");
    }

    /// <summary>
    /// Step 3 — Simulate: S-matrix light propagation runs over the design;
    /// power arrives at the imported component's input pin through the real
    /// connection from the Grating Coupler. The imported component's own
    /// pass-through is blocked by its empty S-matrix (defect #1005).
    /// </summary>
    [Fact]
    public async Task Step3_Simulate_PowerArrivesAtImportedInput()
    {
        var service = new SimulationService();
        var result = await service.RunAsync(_f.Canvas);

        result.Success.ShouldBeTrue(result.ErrorMessage ?? "simulation failed");
        result.FieldResults.ShouldNotBeNull();
        result.LightSourceCount.ShouldBeGreaterThan(0,
            "the Grating Coupler acts as a light source");

        // Light must arrive at the imported waveguide's "in" pin — proving the
        // connection from the GC carries power through the real routing path.
        var importedComponent = _f.GetImportedComponent();
        var inPin = importedComponent.PhysicalPins.Single(p => p.Name == "in");
        result.FieldResults!.ContainsKey(inPin.LogicalPin!.IDInFlow).ShouldBeTrue(
            "the simulation must produce a field value at the imported input pin");
        result.FieldResults[inPin.LogicalPin.IDInFlow].Magnitude
            .ShouldBeGreaterThan(0, "no light arrives at the imported component's input");
    }

    /// <summary>
    /// Step 4 — Save/load: the design round-trips through .lun; the imported
    /// component, its pins, and the connection survive.
    /// </summary>
    [Fact]
    public async Task Step4_SaveLoad_RoundTripPreservesImportedComponent()
    {
        var savePath = await _f.SaveDesign();

        // Load into a fresh canvas with a fresh design scope.
        using var loadHost = new GdsDesignScopeTestHost();
        var loadCanvas = new DesignCanvasViewModel();
        await GdsImportJourneyFixture.LoadFromFile(
            GdsImportJourneyFixture.CreateFileOperations(loadCanvas, loadHost), savePath);

        // The imported component resolves back from the design scope.
        loadCanvas.Components.Count.ShouldBe(2,
            "the imported waveguide plus the Grating Coupler survive the round trip");

        var loadedWg = loadCanvas.Components
            .Select(vm => vm.Component)
            .OfType<Component>()
            .First(c => c is not ComponentGroup && c.PhysicalPins.Any(p => p.Name == "in"));
        loadedWg.PhysicalPins.Select(p => p.Name).ShouldBe(
            new[] { "in", "out" }, ignoreOrder: true);
        loadedWg.WidthMicrometers.ShouldBe(10, 1e-9);
        loadedWg.HeightMicrometers.ShouldBe(4, 1e-9);

        // The connection survived.
        loadCanvas.Connections.Count.ShouldBe(1,
            "the waveguide connection survives the round trip");
    }

    /// <summary>
    /// Step 5 — Re-export: the loaded design exports through the real nazca
    /// path; the script references the imported cell.
    /// </summary>
    [SkippableFact]
    [Trait("Category", "Slow")]
    public async Task Step5_ReExport_ScriptReferencesImportedCell()
    {
        var python = await GdsUserDesignFixture.FindNazcaPythonAsync();
        Skip.If(python == null,
            "No Python with nazca available — the re-export needs the real engine.");

        var savePath = await _f.SaveDesign();

        using var loadHost = new GdsDesignScopeTestHost();
        var loadCanvas = new DesignCanvasViewModel();
        await GdsImportJourneyFixture.LoadFromFile(
            GdsImportJourneyFixture.CreateFileOperations(loadCanvas, loadHost), savePath);

        var script = new SimpleNazcaExporter().Export(
            loadCanvas, library: loadHost.Templates.ToList());

        script.Contains("component_wg(", StringComparison.Ordinal).ShouldBeTrue(
            "the re-exported script must reference the imported cell");
    }
}
