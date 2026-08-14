using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Export;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core;
using CAP_Core.Components.Core;
using CAP_Core.Export;
using CAP_DataAccess.Import.Gds;
using Moq;
using Shouldly;
using UnitTests.Import.Gds;
using UnitTests.Services.GdsImport;
using Xunit;

namespace UnitTests.Persistence;

/// <summary>
/// The .lun round-trip of a GDS-imported circuit (issue #808): import a GDS
/// (components registered into the design scope), place it on the canvas through
/// the real placement executor, save the design (the imported GDS sets ride the
/// .lun), then load it into a FRESH design scope — every component must resolve
/// back to its imported template (<c>TemplateName</c> + <c>PdkSource</c> of the
/// import pdk, not a dropped or fallback component), with positions, rotation,
/// and the grouped (frozen) abutment connection intact. Save/Load wiring mirrors
/// <c>SkippedRouteConnectionPersistenceTests</c>, the import harness mirrors
/// <c>GdsImportServiceTests</c>.
/// </summary>
public class GdsImportDesignRoundTripTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lunima-gds-roundtrip-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public async Task ImportPlaceSaveLoad_ComponentsResolveFromUserPdk_ConnectionRestored()
    {
        var gdsPath = WriteGds();
        using var saveHost = new GdsDesignScopeTestHost();
        var service = saveHost.CreateService(() => Array.Empty<ComponentTemplate>());
        var outcome = await service.ImportAsync(gdsPath, "TOP", null, null);
        outcome.Warnings.ShouldBeEmpty();

        // Place through the real executor: two abutting waveguides, connected, grouped as 'TOP'.
        var saveCanvas = new DesignCanvasViewModel();
        var plan = GdsPlacementPlan.FromOutcome(outcome);
        var report = await new GdsPlacementExecutor(saveCanvas, null, () => saveHost.Templates.ToList())
            .ExecuteAsync(plan);
        report.PlacedCount.ShouldBe(2);
        report.ConnectedCount.ShouldBe(1);
        report.GroupCreated.ShouldBeTrue();

        var savePath = Path.Combine(_root, "circuit.lun");
        await SaveToFile(CreateFileOperations(saveCanvas, saveHost, out _), savePath);

        // Close → reopen into a fresh canvas with a FRESH design scope: the imported
        // GDS sets ride the .lun and re-register the templates before placements resolve.
        using var loadHost = new GdsDesignScopeTestHost();
        var loadCanvas = new DesignCanvasViewModel();
        await LoadFromFile(CreateFileOperations(loadCanvas, loadHost, out _), savePath);

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
            loadHost.Templates.Single(t =>
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
        var script = new SimpleNazcaExporter().Export(loadCanvas, library: loadHost.Templates.ToList());
        script.ShouldContain("component_wgA().put('org'");
        script.ShouldContain("component_wgB().put('org'");
    }

    // ── Harness (mirrors GdsImportServiceTests / SkippedRouteConnectionPersistenceTests) ──

    [Fact]
    public async Task ImportPlaceSaveLoad_TopCellRoutePolygon_RoundTripsAsPinLessFrozenPath()
    {
        var gdsPath = WriteGdsWithRoutePolygon();
        using var saveHost = new GdsDesignScopeTestHost();
        var service = saveHost.CreateService(() => Array.Empty<ComponentTemplate>());
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
        var report = await new GdsPlacementExecutor(saveCanvas, null, () => saveHost.Templates.ToList())
            .ExecuteAsync(plan);
        report.GroupCreated.ShouldBeTrue();

        var savePath = Path.Combine(_root, "circuit-routes.lun");
        await SaveToFile(CreateFileOperations(saveCanvas, saveHost, out _), savePath);

        // Close → reopen into a fresh canvas with a FRESH design scope.
        using var loadHost = new GdsDesignScopeTestHost();
        var loadCanvas = new DesignCanvasViewModel();
        await LoadFromFile(CreateFileOperations(loadCanvas, loadHost, out _), savePath);

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
        // the process default — as ONE verbatim polygon (the path holds the polygon's
        // outline ring, not a centerline — per-edge waveguides would double the lines
        // on every re-import, see GdsReexportIdempotencyTests).
        var script = new SimpleNazcaExporter().Export(loadCanvas, library: loadHost.Templates.ToList());
        script.ShouldContain(
            "nd.Polygon(points=[(10.00,-3.75),(12.00,-3.75),(12.00,-3.25),(10.00,-3.25)], layer=(1, 0)).put(0, 0)");

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
        using var saveHost = new GdsDesignScopeTestHost();
        var service = saveHost.CreateService(() => Array.Empty<ComponentTemplate>());
        var outcome = await service.ImportAsync(gdsPath, "TOP", null, null);
        outcome.Warnings.ShouldContain(w => w.Contains("'logo'") && w.Contains("geometry-only"));

        var saveCanvas = new DesignCanvasViewModel();
        var report = await new GdsPlacementExecutor(saveCanvas, null, () => saveHost.Templates.ToList())
            .ExecuteAsync(GdsPlacementPlan.FromOutcome(outcome));
        report.PlacedCount.ShouldBe(2);
        report.GroupCreated.ShouldBeTrue();

        var savePath = Path.Combine(_root, "circuit-geomonly.lun");
        await SaveToFile(CreateFileOperations(saveCanvas, saveHost, out _), savePath);

        using var loadHost = new GdsDesignScopeTestHost();
        var loadCanvas = new DesignCanvasViewModel();
        await LoadFromFile(CreateFileOperations(loadCanvas, loadHost, out _), savePath);

        var group = loadCanvas.Components.ShouldHaveSingleItem().Component.ShouldBeOfType<ComponentGroup>();
        group.ChildComponents.Count.ShouldBe(2);
        var logo = group.ChildComponents.Single(c => c.PhysicalX == 20);
        logo.HumanReadableName.ShouldBe("logo");
        logo.PhysicalPins.ShouldBeEmpty("a geometry-only component round-trips with zero pins");
        logo.OutlinePolygons.ShouldNotBeNull().ShouldNotBeEmpty();
        loadHost.Templates.Single(t => t.Name == "logo" && t.PdkSource == "GDS Import - circuit-geomonly")
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

    [Fact]
    public async Task ImportPlaceSaveLoad_NonCardinalRotation_SurvivesRoundTrip()
    {
        // Two 10 µm waveguides end-to-end, BOTH rotated 30° (the joint at GDS
        // (7660, 6732) nm): the exact rotation must survive save → load —
        // before, only the discrete quarter-turn was persisted and the pair
        // reloaded unrotated, visibly shifting the bodies (field report).
        Directory.CreateDirectory(_root);
        var gdsPath = Path.Combine(_root, "rotated.gds");
        File.WriteAllBytes(gdsPath, GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("wg", 0, 0, angleDegrees: 30)
                .SRef("wg", 8660, 5000, angleDegrees: 30)
            .EndCell()
            .WaveguideCell("wg")
            .EndLibrary()
            .ToArray());

        using var saveHost = new GdsDesignScopeTestHost();
        var service = saveHost.CreateService(() => Array.Empty<ComponentTemplate>());
        var outcome = await service.ImportAsync(gdsPath, "TOP", null, null);

        var saveCanvas = new DesignCanvasViewModel();
        var report = await new GdsPlacementExecutor(saveCanvas, null, () => saveHost.Templates.ToList())
            .ExecuteAsync(GdsPlacementPlan.FromOutcome(outcome));
        report.GroupCreated.ShouldBeTrue();
        var savedGroup = (ComponentGroup)saveCanvas.Components.Single().Component;
        var savedPositions = savedGroup.ChildComponents
            .SelectMany(c => c.PhysicalPins.Select(p => p.GetAbsolutePosition()))
            .OrderBy(p => p.x).ThenBy(p => p.y)
            .ToList();

        var savePath = Path.Combine(_root, "rotated.lun");
        await SaveToFile(CreateFileOperations(saveCanvas, saveHost, out _), savePath);

        using var loadHost = new GdsDesignScopeTestHost();
        var loadCanvas = new DesignCanvasViewModel();
        await LoadFromFile(CreateFileOperations(loadCanvas, loadHost, out _), savePath);

        var group = loadCanvas.Components.ShouldHaveSingleItem().Component.ShouldBeOfType<ComponentGroup>();
        group.ChildComponents.Count.ShouldBe(2);

        // The exact 30° rotation (app 330°) survived — not snapped back to 0°.
        foreach (var child in group.ChildComponents)
        {
            child.RotationDegrees.ShouldBe(330.0, 0.5,
                "a non-cardinal import rotation must round-trip through .lun");
            child.UnrotatedWidthMicrometers.ShouldBe(10, 1e-9,
                "the unrotated geometry frame rides along for the outline renderer");
            child.UnrotatedHeightMicrometers.ShouldBe(4, 1e-9);
        }

        // Every pin sits exactly where it was before the save.
        var loadedPositions = group.ChildComponents
            .SelectMany(c => c.PhysicalPins.Select(p => p.GetAbsolutePosition()))
            .OrderBy(p => p.x).ThenBy(p => p.y)
            .ToList();
        loadedPositions.Count.ShouldBe(savedPositions.Count);
        foreach (var (loaded, saved) in loadedPositions.Zip(savedPositions))
        {
            loaded.x.ShouldBe(saved.x, 0.01, "pin X must not drift across save/load");
            loaded.y.ShouldBe(saved.y, 0.01, "pin Y must not drift across save/load");
        }

        // The reconstructed joint connection survived with its pins resolved.
        group.InternalPaths.Count(p => p.StartPin is not null && p.EndPin is not null)
            .ShouldBe(1);
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

    /// <summary>
    /// File-operations VM wired to the host's design scope: saving embeds the
    /// scope's imported GDS sets in the .lun, loading restores them into the
    /// host (re-registering the templates) before placements resolve.
    /// </summary>
    private static FileOperationsViewModel CreateFileOperations(
        DesignCanvasViewModel canvas,
        GdsDesignScopeTestHost host,
        out ErrorConsoleService errorConsole)
    {
        errorConsole = new ErrorConsoleService();
        return new FileOperationsViewModel(
            canvas,
            new CommandManager(),
            new SimpleNazcaExporter(),
            new SaxExporter(),
            host.Templates,
            new GdsExportViewModel(new GdsExportService()),
            new PhotonTorchExportViewModel(new PhotonTorchExporter(), canvas),
            null!,
            errorConsole: errorConsole)
        {
            DesignScopedGdsComponents = host.Scope,
        };
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
