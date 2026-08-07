using CAP_DataAccess.Import.Gds;
using Shouldly;
using Xunit;

namespace UnitTests.Import.Gds;

/// <summary>
/// Tests for the label-layer auto-discovery fallback
/// (<c>GdsHierarchyImportSession.DetectWithAnyLayerFallback</c>): when no
/// configured port layer yields any label pin for a cell, the import retries
/// with every text label treated as a pin label and reports the discovered
/// layer(s) as an info note. Fixtures follow <see cref="GdsHierarchyImporterTests"/>
/// (1 db unit = 1 nm, coordinates in database units).
/// </summary>
public class GdsLabelLayerFallbackTests
{
    private const double Tolerance = 1e-6;

    /// <summary>
    /// Foundry-style device cell: 10×4 µm extent with a waveguide core, but the
    /// in/out pin labels live on a foundry layer (235,0) instead of the
    /// configured (1,10)/(501,1) — the shape a big production design arrives in.
    /// </summary>
    private static byte[] FoundryLayerLibrary(int labelLayer, int labelType) => GdsTestWriter.Create()
        .StandardPrologue()
        .BeginCell("TOP")
            .SRef("soa", 0, 0)
        .EndCell()
        .BeginCell("soa")
            .Boundary(1, 0, (0, 1750), (10000, 1750), (10000, 2250), (0, 2250), (0, 1750))
            .Boundary(111, 0, (0, 0), (10000, 0), (10000, 4000), (0, 4000), (0, 0))
            .Text(labelLayer, labelType, "in", 0, 2000)
            .Text(labelLayer, labelType, "out", 10000, 2000)
        .EndCell()
        .EndLibrary()
        .ToArray();

    private static async Task<GdsLibrary> ReadLibraryAsync(byte[] gds) =>
        await new GdsReader().ReadAsync(new MemoryStream(gds));

    [Fact]
    public async Task Explode_LabelsOnlyOnFoundryLayer_FallbackFindsPinsAndNotesTheLayer()
    {
        var library = await ReadLibraryAsync(FoundryLayerLibrary(235, 0));

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        var draft = result.ImportedCellDrafts.ShouldHaveSingleItem();
        draft.Pins.Count.ShouldBe(2, "the fallback treats the (235,0) labels as pin labels");
        draft.Pins[0].Name.ShouldBe("in");
        draft.Pins[0].AngleDegrees.ShouldBe(180, Tolerance);
        draft.Pins[1].Name.ShouldBe("out");
        draft.Pins[1].AngleDegrees.ShouldBe(0, Tolerance);

        var note = result.Infos.Where(i => i.Contains("non-standard layer")).ShouldHaveSingleItem();
        note.ShouldContain("'soa'");
        note.ShouldContain("(235,0)");
        note.ShouldContain("port-layer list");
        result.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task Explode_LabelsOnMultipleFoundryLayers_NoteListsAllLayersSorted()
    {
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("soa", 0, 0)
            .EndCell()
            .BeginCell("soa")
                .Boundary(1, 0, (0, 1750), (10000, 1750), (10000, 2250), (0, 2250), (0, 1750))
                .Boundary(111, 0, (0, 0), (10000, 0), (10000, 4000), (0, 4000), (0, 0))
                .Text(235, 0, "in", 0, 2000)
                .Text(56, 0, "out", 10000, 2000)
            .EndCell()
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        var draft = result.ImportedCellDrafts.ShouldHaveSingleItem();
        draft.Pins.Count.ShouldBe(2);
        var note = result.Infos.Where(i => i.Contains("non-standard layers")).ShouldHaveSingleItem();
        // Layers are reported in deterministic sorted order.
        note.ShouldContain("(56,0), (235,0)");
    }

    [Fact]
    public async Task Explode_LabelsOnConfiguredLayer_NoFallbackAndNoNote()
    {
        var library = await ReadLibraryAsync(FoundryLayerLibrary(1, 10));

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        var draft = result.ImportedCellDrafts.ShouldHaveSingleItem();
        draft.Pins.Count.ShouldBe(2);
        result.Infos.ShouldNotContain(i => i.Contains("non-standard layer"),
            "configured layers win first — the fallback never runs for this cell");
    }

    [Fact]
    public async Task Explode_ConfiguredAndFoundryLabels_ConfiguredWinsWithoutFallback()
    {
        // One label on the configured (1,10) is enough to skip the fallback:
        // the (235,0) label must NOT become a second pin (never mixes).
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("soa", 0, 0)
            .EndCell()
            .BeginCell("soa")
                .Boundary(1, 0, (0, 1750), (10000, 1750), (10000, 2250), (0, 2250), (0, 1750))
                .Boundary(111, 0, (0, 0), (10000, 0), (10000, 4000), (0, 4000), (0, 0))
                .Text(1, 10, "in", 0, 2000)
                .Text(235, 0, "out", 10000, 2000)
            .EndCell()
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        var draft = result.ImportedCellDrafts.ShouldHaveSingleItem();
        // With a configured label pin present the fallback never runs, so the
        // (235,0) "out" label stays invisible as a LABEL pin — the right-edge
        // waveguide touch surfaces through the edge heuristic instead.
        draft.Pins.Count.ShouldBe(2);
        var labelPins = draft.Pins.Where(p => p.Source == DetectedPinSource.Label).ToList();
        labelPins.ShouldHaveSingleItem().Name.ShouldBe("in");
        draft.Pins.ShouldNotContain(p => p.Name == "out",
            "the fallback never mixes: the (235,0) label must not become a second label pin");
        result.Infos.ShouldNotContain(i => i.Contains("non-standard layer"));
    }

    [Fact]
    public async Task Explode_CellWithoutAnyLabels_NoPinsAndNoNote()
    {
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("marker", 0, 0)
            .EndCell()
            .BeginCell("marker")
                .Boundary(111, 0, (0, 0), (10000, 0), (10000, 4000), (0, 4000), (0, 0))
            .EndCell()
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        var draft = result.ImportedCellDrafts.ShouldHaveSingleItem();
        draft.Pins.ShouldBeEmpty("no labels at all — the fallback has nothing to discover");
        draft.Outlines.ShouldNotBeEmpty("the geometry still becomes the draft's outlines");
        result.Infos.ShouldNotContain(i => i.Contains("non-standard layer"));
    }

    [Fact]
    public async Task BlackBox_LabelsOnlyOnFoundryLayer_FallbackFindsPinsAndNotesTheLayer()
    {
        var library = await ReadLibraryAsync(FoundryLayerLibrary(233, 0));

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions
        {
            Mode = GdsHierarchyImportMode.BlackBox,
        });

        var draft = result.ImportedCellDrafts.ShouldHaveSingleItem();
        draft.Pins.Count.ShouldBe(2, "black-box detection runs the same any-layer fallback");
        result.Infos.ShouldContain(i => i.Contains("non-standard layer") && i.Contains("(233,0)"));
    }

    [Fact]
    public async Task TopLevelPorts_MultiLineMetadataText_IsNotAPort()
    {
        // nazca writes a metadata blob ("cellname: …\nfoundry_pdk: …") as top-cell
        // TEXT on a configured label layer; a pin name can never span lines, so it
        // must not surface as an external port (it poisoned junction notes before).
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .Boundary(1, 0, (0, 0), (20000, 0), (20000, 4000), (0, 4000), (0, 0))
                .Text(1, 10, "in0", 0, 2000)
                .Text(1, 10, "cellname: big_design\nfoundry_pdk: bigfoundry", 10000, 2000)
            .EndCell()
            .EndLibrary()
            .ToArray());

        var session = new GdsHierarchyImportSession(library, "TOP", new GdsHierarchyImportOptions());
        var ports = session.GetTopLevelPorts();

        var port = ports.ShouldHaveSingleItem(
            "the multi-line metadata blob is filtered — only the real port label remains");
        port.Name.ShouldBe("in0");
    }
}
