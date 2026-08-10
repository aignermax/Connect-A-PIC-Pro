using CAP.Avalonia.Commands;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.GdsImport;
using CAP.Avalonia.ViewModels.Library;
using CAP_DataAccess.Import.Gds;
using CAP_DataAccess.Import.Gds.LayerCensus;
using Shouldly;
using UnitTests.Import.Gds;
using Xunit;

namespace UnitTests.Services.GdsImport;

/// <summary>
/// Integration tests against a SYNTHETIC foundry-style GDS file: nazca-shaped
/// (bbox anchor labels + parameter annotations stamped next to the real port
/// labels on one foundry text layer), a waveguide core drawn on a layer number
/// our union convention table knows only as metal, and a thick metal trace on
/// yet another number. Inspired by the structure of a confidential foundry
/// file — this fixture contains no real foundry data. Guards the two field
/// reports: ghost pins from helper labels on a configured port layer, and
/// waveguide layers silently auto-assigned to metal.
/// </summary>
public class GdsFoundryStyleFileTests : IDisposable
{
    private const int WaveguideLayer = 11;   // union table says "SiEPIC M1" — the collision
    private const int MetalLayer = 12;       // union table says "SiEPIC M2 router" — here truly metal
    private const int TextLayer = 56;        // foundry port-label layer
    private const int BboxLayer = 111;       // neutral bbox-sizing boundary

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lunima-gdsfoundry-" + Guid.NewGuid().ToString("N"));
    private readonly GdsDesignScopeTestHost _host = new();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        _host.Dispose();
    }

    /// <summary>
    /// TOP with two device instances, one optical route on the collision layer
    /// and one thick metal trace; the device cell carries its waveguide core on
    /// the collision layer plus real port labels surrounded by nazca helper
    /// labels (anchors "tl"/"tr"/… and parameter annotations "R:0.0001"/"n:1.0")
    /// on the foundry text layer. (1 db unit = 1 nm.)
    /// </summary>
    private static byte[] SyntheticFoundryLibrary() => GdsTestWriter.Create()
        .StandardPrologue()
        .BeginCell("TOP")
            .SRef("dev", 0, 0)
            .SRef("dev", 60000, 0)
            .Path(WaveguideLayer, 0, 500, 0, (10000, 2000), (50000, 2000))
            .Path(MetalLayer, 0, 20000, 0, (10000, 20000), (50000, 20000))
        .EndCell()
        .BeginCell("dev")
            .Boundary(WaveguideLayer, 0, (0, 1750), (10000, 1750), (10000, 2250), (0, 2250), (0, 1750))
            .Boundary(BboxLayer, 0, (0, 0), (10000, 0), (10000, 4000), (0, 4000), (0, 0))
            .Text(TextLayer, 0, "o1", 0, 2000)
            .Text(TextLayer, 0, "o2", 10000, 2000)
            .Text(TextLayer, 0, "tl", 0, 4000)
            .Text(TextLayer, 0, "tr", 10000, 4000)
            .Text(TextLayer, 0, "bl", 0, 0)
            .Text(TextLayer, 0, "br", 10000, 0)
            .Text(TextLayer, 0, "R:0.0001", 5000, 3000)
            .Text(TextLayer, 0, "n:1.0", 5000, 1000)
        .EndCell()
        .EndLibrary()
        .ToArray();

    private static async Task<GdsLibrary> ReadLibraryAsync(byte[] gds) =>
        await new GdsReader().ReadAsync(new MemoryStream(gds));

    private async Task<GdsImportDialogViewModel> AnalyzedDialog()
    {
        Directory.CreateDirectory(_root);
        var gdsPath = Path.Combine(_root, "foundry.gds");
        File.WriteAllBytes(gdsPath, SyntheticFoundryLibrary());
        var service = _host.CreateService();
        var executor = new GdsPlacementExecutor(
            new DesignCanvasViewModel(), new CommandManager(), () => new List<ComponentTemplate>());
        var vm = new GdsImportDialogViewModel(gdsPath, service, executor);
        await vm.StartAnalysisAsync();
        vm.HasError.ShouldBeFalse(vm.ErrorText);
        return vm;
    }

    [Fact]
    public async Task Explode_ConfiguredFoundryTextLayer_YieldsOnlyRealPortPins_NoGhostPins()
    {
        // The accepted/auto-applied port-layer suggestion lands in the field —
        // and with it every nazca helper label living on that layer. The ghost
        // filter must strip them even on the configured path: pins are exactly
        // the two real ports, never tl/br/R:0.0001/n:1.0.
        var library = await ReadLibraryAsync(SyntheticFoundryLibrary());
        var options = new GdsHierarchyImportOptions
        {
            PinDetection = new GdsPinDetectionOptions
            {
                PortLayers = [(1, 10), (501, 1), (TextLayer, 0)],
            },
        };

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", options);

        var draft = result.ImportedCellDrafts.ShouldHaveSingleItem();
        draft.Pins.Select(p => p.Name).ShouldBe(new[] { "o1", "o2" }, ignoreOrder: true);
    }

    [Fact]
    public async Task Suggestions_CollisionLayerMetalClaim_StaysMedium_NeverHigh()
    {
        var library = await ReadLibraryAsync(SyntheticFoundryLibrary());
        var suggestions = GdsLayerSuggestionEngine.Build(
            library, "TOP", GdsLayerCensus.Build(library));

        // (11,0) carries the waveguide core + an optical route in this file,
        // yet the union table calls it metal — a bare-number convention guess
        // that must never become an auto-applied high-confidence claim.
        var collision = suggestions.Single(s =>
            s.Layer == WaveguideLayer && s.Datatype == 0 && s.Role == GdsLayerRole.Metal);
        collision.Confidence.ShouldBe(GdsSuggestionConfidence.Medium);
        suggestions.ShouldNotContain(s => s.Layer == WaveguideLayer && s.Role == GdsLayerRole.Waveguide);

        // The text-backed port layer is the reliable, auto-appliable one; the
        // real metal trace is a confirmable convention claim.
        suggestions.ShouldContain(s =>
            s.Layer == TextLayer && s.Role == GdsLayerRole.PortLabels
            && s.Confidence == GdsSuggestionConfidence.High);
        suggestions.ShouldContain(s =>
            s.Layer == MetalLayer && s.Role == GdsLayerRole.Metal
            && s.Confidence == GdsSuggestionConfidence.Medium);
    }

    [Fact]
    public async Task Dialog_Analysis_AutoAppliesPorts_ButNeverTheCollisionMetal()
    {
        var vm = await AnalyzedDialog();

        vm.PortLayersText.ShouldContain($"{TextLayer},0");
        vm.MetalLayersText.ShouldBe("11,0; 12,0; 13,0");
        vm.WaveguideLayersText.ShouldBe("1,0; 1111,0");

        var chip = vm.SuggestionChips.Single(c =>
            c.Suggestion.Layer == WaveguideLayer && c.Suggestion.Role == GdsLayerRole.Metal);
        chip.Suggestion.Confidence.ShouldBe(GdsSuggestionConfidence.Medium);
    }
}
