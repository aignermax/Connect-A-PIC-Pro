using System.Collections.ObjectModel;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Export;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core;
using CAP_Core.Components.Core;
using CAP_Core.Export;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using CAP_DataAccess.Import.Gds;
using Moq;
using Shouldly;
using UnitTests.Import.Gds;
using Xunit;

namespace UnitTests.Persistence;

/// <summary>
/// The .lun round-trip of a GDS-imported circuit (issue #808): import a GDS
/// (components registered into a user PDK), place it on the canvas through the
/// real placement executor, save the design, then load it into a fresh canvas —
/// every component must resolve back to its imported template
/// (<c>TemplateName</c> + <c>PdkSource</c> of the user PDK, not a dropped or
/// fallback component), with positions, rotation, and the grouped (frozen)
/// abutment connection intact. Save/Load wiring mirrors
/// <c>SkippedRouteConnectionPersistenceTests</c>, the import harness mirrors
/// <c>GdsImportServiceTests</c>.
/// </summary>
public class GdsImportDesignRoundTripTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lunima-gds-roundtrip-" + Guid.NewGuid().ToString("N"));
    private readonly string _prefsPath =
        Path.Combine(Path.GetTempPath(), $"lunima-gds-roundtrip-prefs-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        if (File.Exists(_prefsPath)) File.Delete(_prefsPath);
    }

    [Fact]
    public async Task ImportPlaceSaveLoad_ComponentsResolveFromUserPdk_ConnectionRestored()
    {
        var gdsPath = WriteGds();
        var sink = new LibrarySink(_prefsPath);
        var service = new GdsImportService(Store(), () => Array.Empty<ComponentTemplate>(), sink.Register);
        var outcome = await service.ImportAsync(gdsPath, "TOP", null, null);
        outcome.Warnings.ShouldBeEmpty();

        // Place through the real executor: two abutting waveguides, connected, grouped as 'TOP'.
        var saveCanvas = new DesignCanvasViewModel();
        var plan = GdsPlacementPlan.FromOutcome(outcome);
        var report = await new GdsPlacementExecutor(saveCanvas, null, () => sink.Templates.ToList())
            .ExecuteAsync(plan);
        report.PlacedCount.ShouldBe(2);
        report.ConnectedCount.ShouldBe(1);
        report.GroupCreated.ShouldBeTrue();

        var savePath = Path.Combine(_root, "circuit.lun");
        await SaveToFile(CreateFileOperations(saveCanvas, sink.Templates, out _), savePath);

        // Close → reopen into a fresh canvas against the same library (the user PDK is loaded).
        var loadCanvas = new DesignCanvasViewModel();
        await LoadFromFile(CreateFileOperations(loadCanvas, sink.Templates, out _), savePath);

        // The design round-trips as the 'TOP' group with both imported components.
        var groupVm = loadCanvas.Components.ShouldHaveSingleItem();
        var group = groupVm.Component.ShouldBeOfType<ComponentGroup>();
        group.GroupName.ShouldBe("TOP");
        group.ChildComponents.Count.ShouldBe(2);

        var wgA = group.ChildComponents.Single(c => c.PhysicalX == 0);
        var wgB = group.ChildComponents.Single(c => c.PhysicalX == 10);
        foreach (var (child, expectedName) in new[] { (wgA, "wgA"), (wgB, "wgB") })
        {
            // Resolution proof: the component carries the template's synthesized nazca
            // function name and its library template is the user PDK's — save/load matched
            // TemplateName + PdkSource, anything else would have failed to load.
            child.HumanReadableName.ShouldBe(expectedName);
            child.NazcaFunctionName.ShouldBe($"nazca_{expectedName.ToLowerInvariant()}");
            child.WidthMicrometers.ShouldBe(10);
            child.HeightMicrometers.ShouldBe(4);
            child.PhysicalY.ShouldBe(0);
            child.RotationDegrees.ShouldBe(0);
            child.PhysicalPins.Select(p => p.Name).ShouldBe(new[] { "in", "out" }, ignoreOrder: true);
            // The load-path outline contract: outlines ride the resolved template onto the
            // component (GDS fixture: core stripe on layer 1, extent rectangle on layer 111).
            var outlines = child.OutlinePolygons.ShouldNotBeNull();
            outlines.Count.ShouldBeGreaterThan(0);
            outlines.Select(o => o.Layer).ShouldBe(new[] { 1, 111 }, ignoreOrder: true);
            sink.Templates.Single(t =>
                t.Name == expectedName && t.PdkSource == "GDS Import - circuit")
                .RawCode.ShouldContain($"cellname=\"{expectedName}\"");
        }

        // The abutment connection (frozen into the group on placement) is restored.
        group.InternalPaths.Count.ShouldBe(1);
        var frozen = group.InternalPaths[0];
        frozen.StartPin?.ParentComponent.ShouldBeSameAs(wgA);
        frozen.EndPin?.ParentComponent.ShouldBeSameAs(wgB);

        // And the reloaded design still exports through the raw-code inlining —
        // the resolved templates carry their RawCode across the round trip.
        var script = new SimpleNazcaExporter().Export(loadCanvas, library: sink.Templates.ToList());
        script.ShouldContain("component_wgA().put('org'");
        script.ShouldContain("component_wgB().put('org'");
    }

    // ── Harness (mirrors GdsImportServiceTests / SkippedRouteConnectionPersistenceTests) ──

    [Fact]
    public async Task ImportPlaceSaveLoad_TopCellRoutePolygon_RoundTripsAsPinLessFrozenPath()
    {
        var gdsPath = WriteGdsWithRoutePolygon();
        var sink = new LibrarySink(_prefsPath);
        var service = new GdsImportService(Store(), () => Array.Empty<ComponentTemplate>(), sink.Register);
        var outcome = await service.ImportAsync(gdsPath, "TOP", null, null);

        // The top cell's own (1,0) polygon comes back as frozen route geometry.
        // (The report moved to the INFO channel: importable geometry is good news,
        // not a warning — see GdsImportReporter.)
        outcome.Warnings.ShouldBeEmpty();
        outcome.Infos.ShouldContain(i => i.Contains("imported as frozen paths (not re-routable)"));
        outcome.TopCellWaveguidePolygons.ShouldHaveSingleItem();

        // Place: the polygon becomes a pin-less frozen path on the 'TOP' group.
        var saveCanvas = new DesignCanvasViewModel();
        var plan = GdsPlacementPlan.FromOutcome(outcome);
        var report = await new GdsPlacementExecutor(saveCanvas, null, () => sink.Templates.ToList())
            .ExecuteAsync(plan);
        report.GroupCreated.ShouldBeTrue();

        var savePath = Path.Combine(_root, "circuit-routes.lun");
        await SaveToFile(CreateFileOperations(saveCanvas, sink.Templates, out _), savePath);

        // Close → reopen into a fresh canvas against the same library.
        var loadCanvas = new DesignCanvasViewModel();
        await LoadFromFile(CreateFileOperations(loadCanvas, sink.Templates, out _), savePath);

        var group = loadCanvas.Components.ShouldHaveSingleItem().Component.ShouldBeOfType<ComponentGroup>();
        group.InternalPaths.Count.ShouldBe(2, "the frozen abutment connection plus the imported route outline");

        // The route outline round-tripped with exact coordinates and no pins.
        var routePath = group.InternalPaths.Single(p => p.StartPin is null);
        routePath.EndPin.ShouldBeNull("imported route geometry is pin-less on BOTH ends");
        routePath.Path.Segments.Select(s => (s.StartPoint.X, s.StartPoint.Y, s.EndPoint.X, s.EndPoint.Y))
            .ShouldBe(new[]
            {
                (10.0, 3.75, 12.0, 3.75),
                (12.0, 3.75, 12.0, 3.25),
                (12.0, 3.25, 10.0, 3.25),
                (10.0, 3.25, 10.0, 3.75),
            });

        // The source polygon's (layer, datatype) rode along the whole way: import →
        // frozen path → .lun → reload (the fixture's route stub sits on (1, 0)).
        routePath.Layer.ShouldBe(1);
        routePath.DataType.ShouldBe(0);

        // …and the reloaded design exports the outline back on its OWN layer, not
        // the process default — one tagged segment per traced outline edge.
        var script = new SimpleNazcaExporter().Export(loadCanvas, library: sink.Templates.ToList());
        script.ShouldContain("nd.strt(length=2.00, layer=(1, 0)).put(");
        script.ShouldContain("nd.strt(length=0.50, layer=(1, 0)).put(");

        // The pinned abutment connection survived the same round-trip unchanged.
        var abutment = group.InternalPaths.Single(p => p.StartPin is not null);
        abutment.StartPin!.Name.ShouldBe("out");
        abutment.EndPin!.Name.ShouldBe("in");
    }

    [Fact]
    public async Task ImportPlaceSaveLoad_GeometryOnlyComponent_RoundTripsWithZeroPins()
    {
        // A pin-less foundry cell (logo) next to a pinned waveguide: the
        // geometry-only component must survive the .lun round-trip like any
        // other — resolving back to its imported template with its outlines.
        var gdsPath = WriteGdsMixedWithPinlessCell();
        var sink = new LibrarySink(_prefsPath);
        var service = new GdsImportService(Store(), () => Array.Empty<ComponentTemplate>(), sink.Register);
        var outcome = await service.ImportAsync(gdsPath, "TOP", null, null);
        outcome.Warnings.ShouldContain(w => w.Contains("'logo'") && w.Contains("geometry-only"));

        var saveCanvas = new DesignCanvasViewModel();
        var report = await new GdsPlacementExecutor(saveCanvas, null, () => sink.Templates.ToList())
            .ExecuteAsync(GdsPlacementPlan.FromOutcome(outcome));
        report.PlacedCount.ShouldBe(2);
        report.GroupCreated.ShouldBeTrue();

        var savePath = Path.Combine(_root, "circuit-geomonly.lun");
        await SaveToFile(CreateFileOperations(saveCanvas, sink.Templates, out _), savePath);

        var loadCanvas = new DesignCanvasViewModel();
        await LoadFromFile(CreateFileOperations(loadCanvas, sink.Templates, out _), savePath);

        var group = loadCanvas.Components.ShouldHaveSingleItem().Component.ShouldBeOfType<ComponentGroup>();
        group.ChildComponents.Count.ShouldBe(2);
        var logo = group.ChildComponents.Single(c => c.PhysicalX == 20);
        logo.HumanReadableName.ShouldBe("logo");
        logo.PhysicalPins.ShouldBeEmpty("a geometry-only component round-trips with zero pins");
        logo.OutlinePolygons.ShouldNotBeNull().ShouldNotBeEmpty();
        sink.Templates.Single(t => t.Name == "logo" && t.PdkSource == "GDS Import - circuit-geomonly")
            .RawCode.ShouldNotBeNull().ShouldContain("cellname=\"logo\"");

        // The top-cell route stub (touches no pins) survived as a pin-less frozen path.
        var frozen = group.InternalPaths.ShouldHaveSingleItem();
        frozen.StartPin.ShouldBeNull();
        frozen.EndPin.ShouldBeNull();
    }

    /// <summary>
    /// TOP with one pinned waveguide (wgA), one pin-less geometry cell ("logo":
    /// only an extent rectangle on (111,0)), and a top-cell route stub on (1,0)
    /// that touches no pin (same placement as <see cref="WriteGdsWithRoutePolygon"/>).
    /// </summary>
    private string WriteGdsMixedWithPinlessCell()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "circuit-geomonly.gds");
        var content = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("wgA", 0, 0)
                .SRef("logo", 20000, 0)
                .Boundary(1, 0, (10000, 250), (12000, 250), (12000, 750), (10000, 750), (10000, 250))
            .EndCell()
            .WaveguideCell("wgA")
            .BeginCell("logo")
                .Boundary(111, 0, (0, 0), (5000, 0), (5000, 5000), (0, 5000), (0, 0))
            .EndCell()
            .EndLibrary()
            .ToArray();
        File.WriteAllBytes(path, content);
        return path;
    }

    // ── Harness (mirrors GdsImportServiceTests / SkippedRouteConnectionPersistenceTests) ──

    /// <summary>TOP with two abutting 10×4 µm waveguide cells (wgA → wgB), gdsfactory-style.</summary>
    private string WriteGds()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "circuit.gds");
        var content = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("wgA", 0, 0)
                .SRef("wgB", 10000, 0)
            .EndCell()
            .WaveguideCell("wgA")
            .WaveguideCell("wgB")
            .EndLibrary()
            .ToArray();
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>
    /// The standard fixture plus the top cell's OWN route polygon on (1,0): a
    /// 2×0.5 µm stub (GDS x 10…12, y 0.25…0.75) — the shape our exporters
    /// flatten routed waveguides into. It sits at app y ∈ [3.25, 3.75], 1.25 µm
    /// off the pin line: both pins of the wgA↔wgB abutment sit at app (10, 2)
    /// and the route-connectivity touch tolerance is 1.0 µm, so the stub touches
    /// NO pin and stays a pin-less frozen path instead of being consumed as a
    /// route-derived connection.
    /// </summary>
    private string WriteGdsWithRoutePolygon()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "circuit-routes.gds");
        var content = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("wgA", 0, 0)
                .SRef("wgB", 10000, 0)
                .Boundary(1, 0, (10000, 250), (12000, 250), (12000, 750), (10000, 750), (10000, 250))
            .EndCell()
            .WaveguideCell("wgA")
            .WaveguideCell("wgB")
            .EndLibrary()
            .ToArray();
        File.WriteAllBytes(path, content);
        return path;
    }

    private UserPdkStore Store() => new(
        Path.Combine(_root, "user-pdks"), new PdkJsonSaver(), new PdkLoader());

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

    private static FileOperationsViewModel CreateFileOperations(
        DesignCanvasViewModel canvas,
        ObservableCollection<ComponentTemplate> library,
        out ErrorConsoleService errorConsole)
    {
        errorConsole = new ErrorConsoleService();
        return new FileOperationsViewModel(
            canvas,
            new CommandManager(),
            new SimpleNazcaExporter(),
            new SaxExporter(),
            library,
            new GdsExportViewModel(new GdsExportService()),
            new PhotonTorchExportViewModel(new PhotonTorchExporter(), canvas),
            null!,
            errorConsole: errorConsole);
    }

    private static async Task SaveToFile(FileOperationsViewModel vm, string filePath)
    {
        var dialog = new Mock<IFileDialogService>();
        dialog.Setup(f => f.ShowSaveFileDialogAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(filePath);
        vm.FileDialogService = dialog.Object;
        await vm.SaveDesignAsCommand.ExecuteAsync(null);
        File.Exists(filePath).ShouldBeTrue();
    }

    private static async Task LoadFromFile(FileOperationsViewModel vm, string filePath)
    {
        var dialog = new Mock<IFileDialogService>();
        dialog.Setup(f => f.ShowOpenFileDialogAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(filePath);
        vm.FileDialogService = dialog.Object;
        await vm.LoadDesignCommand.ExecuteAsync(null);
    }
}

/// <summary>GDS fixture cell builders (same shape as GdsImportServiceTests' waveguide cell).</summary>
file static class GdsRoundTripTestCells
{
    /// <summary>
    /// 10×4 µm gdsfactory-style waveguide: a 0.5 µm core stripe on the waveguide
    /// layer (1,0), an extent rectangle on (111,0), and in/out port labels on (1,10).
    /// </summary>
    public static GdsTestWriter WaveguideCell(this GdsTestWriter writer, string name) =>
        writer
            .BeginCell(name)
                .Boundary(1, 0, (0, 1750), (10000, 1750), (10000, 2250), (0, 2250), (0, 1750))
                .Boundary(111, 0, (0, 0), (10000, 0), (10000, 4000), (0, 4000), (0, 0))
                .Text(1, 10, "in", 0, 2000)
                .Text(1, 10, "out", 10000, 2000)
            .EndCell();
}
