using System.Collections.ObjectModel;
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
using UnitTests.Import.Gds;
using Xunit;

namespace UnitTests.Services.GdsImport;

/// <summary>
/// Connection source layers (the D half of the layer round-trip): a route-derived
/// connection whose source polygons share ONE (layer, datatype) keeps that layer as a
/// tag — whether it is re-created with Lunima routing (the default) or kept as the
/// frozen cached route — so the export emits the geometry on the ORIGINAL layer
/// instead of the process default. Mixed-layer source networks are ambiguous and stay
/// untagged (historical default behavior). Harness mirrors
/// <see cref="UnitTests.Persistence.GdsImportDesignRoundTripTests"/>.
/// </summary>
public class GdsImportSourceLayerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lunima-gds-sourcelayer-" + Guid.NewGuid().ToString("N"));
    private readonly string _prefsPath =
        Path.Combine(Path.GetTempPath(), $"lunima-gds-sourcelayer-prefs-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        if (File.Exists(_prefsPath)) File.Delete(_prefsPath);
    }

    [Fact]
    public async Task RerouteMode_TaggedConnection_ExportsOnSourceLayer()
    {
        var (canvas, report) = await ImportAndPlace(WriteGdsBridgedOnCustomLayer());

        report.RouteDerivedCount.ShouldBe(1);
        var group = canvas.Components.ShouldHaveSingleItem().Component.ShouldBeOfType<ComponentGroup>();
        var frozen = group.InternalPaths.ShouldHaveSingleItem(
            "the bridge polygon is consumed by route derivation — one pinned connection");
        frozen.StartPin.ShouldNotBeNull();
        // The tag survived re-routing AND grouping (captured into the frozen path).
        frozen.Layer.ShouldBe(3);
        frozen.DataType.ShouldBe(0);

        var script = new SimpleNazcaExporter().Export(canvas);
        script.ShouldContain("layer=(3, 0)",
            customMessage: "the re-created Lunima route exports on the source layer, not the process default");
    }

    [Fact]
    public async Task FrozenMode_TaggedConnection_KeepsSourceLayerOnCachedRoute()
    {
        var (canvas, report) = await ImportAndPlace(
            WriteGdsBridgedOnCustomLayer(), rerouteImportedConnections: false);

        report.RouteDerivedCount.ShouldBe(1);
        var group = canvas.Components.ShouldHaveSingleItem().Component.ShouldBeOfType<ComponentGroup>();
        var frozen = group.InternalPaths.ShouldHaveSingleItem();
        frozen.Layer.ShouldBe(3);
        frozen.DataType.ShouldBe(0);

        var script = new SimpleNazcaExporter().Export(canvas);
        script.ShouldContain("layer=(3, 0)");
    }

    [Fact]
    public async Task MixedLayerSourceNetwork_StaysUntagged()
    {
        // The bridge network chains a (1, 0) polygon with an overlapping (3, 0)
        // polygon — no single source layer, so no tag and no layer override anywhere.
        var (canvas, report) = await ImportAndPlace(
            WriteGdsBridgedByMixedLayerNetwork(), rerouteImportedConnections: false);

        report.RouteDerivedCount.ShouldBe(1);
        var group = canvas.Components.ShouldHaveSingleItem().Component.ShouldBeOfType<ComponentGroup>();
        var frozen = group.InternalPaths.ShouldHaveSingleItem();
        frozen.Layer.ShouldBeNull("a mixed-layer source is ambiguous — the process default applies");
        frozen.DataType.ShouldBeNull();

        var script = new SimpleNazcaExporter().Export(canvas);
        script.ShouldNotContain("layer=(3, 0)");
        script.ShouldNotContain("layer=(1, 0)");
    }

    // ── Harness (mirrors GdsImportDesignRoundTripTests) ──────────────────────

    private async Task<(DesignCanvasViewModel Canvas, GdsPlacementReport Report)> ImportAndPlace(
        string gdsPath, bool rerouteImportedConnections = true)
    {
        var sink = new LibrarySink(_prefsPath + Guid.NewGuid().ToString("N")[..6]);
        var service = new GdsImportService(Store(), () => Array.Empty<ComponentTemplate>(), sink.Register);
        var outcome = await service.ImportAsync(
            gdsPath, "TOP",
            new GdsHierarchyImportOptions { RouteLayers = [(1, 0), (3, 0)] }, null);
        outcome.Warnings.ShouldBeEmpty();

        var canvas = new DesignCanvasViewModel();
        var report = await new GdsPlacementExecutor(canvas, null, () => sink.Templates.ToList())
            .ExecuteAsync(GdsPlacementPlan.FromOutcome(outcome),
                rerouteImportedConnections: rerouteImportedConnections);
        report.GroupCreated.ShouldBeTrue();
        return (canvas, report);
    }

    /// <summary>
    /// Two waveguide cells 5 µm apart, bridged by a top-cell stripe on the CUSTOM
    /// waveguide layer (3, 0) whose edges pass exactly through wgA.out (10, 2) and
    /// wgB.in (15, 2) — same geometry as the dialog tests' (1, 0) bridge fixture.
    /// </summary>
    private string WriteGdsBridgedOnCustomLayer() => WriteGds("bridged-3-0.gds", GdsTestWriter.Create()
        .StandardPrologue()
        .BeginCell("TOP")
            .SRef("wgA", 0, 0)
            .SRef("wgB", 15000, 0)
            .Boundary(3, 0, (10000, 1750), (15000, 1750), (15000, 2250), (10000, 2250), (10000, 1750))
        .EndCell()
        .WaveguideCell("wgA")
        .WaveguideCell("wgB")
        .EndLibrary()
        .ToArray());

    /// <summary>
    /// Same layout, but the bridge is a two-polygon network spanning BOTH route
    /// layers: the (1, 0) and (3, 0) stripes overlap (x 11.7…12.3 µm) and chain into
    /// one network — a route-derived connection with an ambiguous source layer.
    /// </summary>
    private string WriteGdsBridgedByMixedLayerNetwork() => WriteGds("bridged-mixed.gds", GdsTestWriter.Create()
        .StandardPrologue()
        .BeginCell("TOP")
            .SRef("wgA", 0, 0)
            .SRef("wgB", 15000, 0)
            .Boundary(1, 0, (10000, 1750), (12300, 1750), (12300, 2250), (10000, 2250), (10000, 1750))
            .Boundary(3, 0, (11700, 1750), (15000, 1750), (15000, 2250), (11700, 2250), (11700, 1750))
        .EndCell()
        .WaveguideCell("wgA")
        .WaveguideCell("wgB")
        .EndLibrary()
        .ToArray());

    private string WriteGds(string fileName, byte[] content)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, fileName);
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
}

/// <summary>GDS fixture cell builders (same shape as the other import test fixtures).</summary>
file static class GdsSourceLayerTestCells
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
