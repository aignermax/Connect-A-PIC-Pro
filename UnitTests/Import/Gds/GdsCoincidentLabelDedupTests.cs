using CAP_DataAccess.Import.Gds;
using Shouldly;
using Xunit;

namespace UnitTests.Import.Gds;

/// <summary>
/// Tests for the coincident-label dedup
/// (<c>GdsHierarchyImportSession.CollapseCoincidentLabels</c>): foundry layouts
/// stack helper/marker labels on the same anchor as the real pin label, and
/// the stack must collapse into ONE pin — otherwise every 2-pin route network
/// inflates into a &gt;2-pin junction and stays frozen instead of becoming a
/// connection. Fixtures follow <see cref="GdsHierarchyImporterTests"/>
/// (1 db unit = 1 nm, coordinates in database units).
/// </summary>
public class GdsCoincidentLabelDedupTests
{
    private const double Tolerance = 1e-6;

    private static async Task<GdsLibrary> ReadLibraryAsync(byte[] gds) =>
        await new GdsReader().ReadAsync(new MemoryStream(gds));

    [Fact]
    public async Task GetCellPins_CoincidentLabels_ConfiguredLayerWinsWithNote()
    {
        // Helper "lc" on (99,0) is written FIRST, then TWO configured labels —
        // (501,1) "x1" and (1,10) "o1" — share its anchor. The first configured
        // label in file order wins the stack. Without the dedup both configured
        // labels become pins and the count reads 3, not 2 — the count gates the
        // feature.
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("soa", 0, 0)
            .EndCell()
            .BeginCell("soa")
                .Boundary(1, 0, (0, 1750), (10000, 1750), (10000, 2250), (0, 2250), (0, 1750))
                .Boundary(111, 0, (0, 0), (10000, 0), (10000, 4000), (0, 4000), (0, 0))
                .Text(99, 0, "lc", 0, 2000)
                .Text(501, 1, "x1", 0, 2000)
                .Text(1, 10, "o1", 0, 2000)
                .Text(1, 10, "i1", 10000, 2000)
            .EndCell()
            .EndLibrary()
            .ToArray());
        var session = new GdsHierarchyImportSession(library, "TOP", new GdsHierarchyImportOptions());

        var pins = session.GetCellPins("soa", session.GetCellBBox("soa"));

        pins.Count.ShouldBe(2, "the 3-label stack collapses into one pin; the labels suppress the edge heuristic");
        pins[0].Name.ShouldBe("x1");
        pins[0].Source.ShouldBe(DetectedPinSource.Label);
        pins[1].Name.ShouldBe("i1");
        var note = session.Infos.Where(i => i.Contains("coincident labels merged")).ShouldHaveSingleItem();
        note.ShouldContain("'x1' kept from layer (501,1)");
        note.ShouldContain("2 labels ignored");
    }

    [Fact]
    public async Task GetCellPins_CoincidentLabelsOnNonConfiguredLayers_FirstLabelWinsViaFallback()
    {
        // No configured-layer label anywhere: the any-layer fallback discovers
        // the pins, and the stack collapses to its FIRST label in file order.
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("soa", 0, 0)
            .EndCell()
            .BeginCell("soa")
                .Boundary(1, 0, (0, 1750), (10000, 1750), (10000, 2250), (0, 2250), (0, 1750))
                .Boundary(111, 0, (0, 0), (10000, 0), (10000, 4000), (0, 4000), (0, 0))
                .Text(233, 0, "o1", 0, 2000)
                .Text(99, 0, "lc", 0, 2000)
                .Text(233, 0, "i1", 10000, 2000)
            .EndCell()
            .EndLibrary()
            .ToArray());
        var session = new GdsHierarchyImportSession(library, "TOP", new GdsHierarchyImportOptions());

        var pins = session.GetCellPins("soa", session.GetCellBBox("soa"));

        pins.Count.ShouldBe(2, "one pin per anchor, not one per stacked label");
        pins[0].Name.ShouldBe("o1");
        pins[1].Name.ShouldBe("i1");
        var note = session.Infos.Where(i => i.Contains("coincident labels merged")).ShouldHaveSingleItem();
        note.ShouldContain("'o1' kept from layer (233,0)");
        var fallbackNote = session.Infos.Where(i => i.Contains("non-standard layer")).ShouldHaveSingleItem();
        fallbackNote.ShouldContain("(233,0)");
        // The dropped helper label's layer must not pollute the discovered-layer list.
        fallbackNote.ShouldNotContain("(99,0)");
    }

    [Fact]
    public async Task GetCellPins_CoincidentLabelsOnSameConfiguredLayer_BothKeptWithoutNote()
    {
        // Two labels tied on the SAME layer at the same anchor are real
        // duplicates — they must not silently merge.
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("soa", 0, 0)
            .EndCell()
            .BeginCell("soa")
                .Boundary(1, 0, (0, 1750), (10000, 1750), (10000, 2250), (0, 2250), (0, 1750))
                .Boundary(111, 0, (0, 0), (10000, 0), (10000, 4000), (0, 4000), (0, 0))
                .Text(1, 10, "o1", 0, 2000)
                .Text(1, 10, "o1b", 0, 2000)
                .Text(1, 10, "i1", 10000, 2000)
            .EndCell()
            .EndLibrary()
            .ToArray());
        var session = new GdsHierarchyImportSession(library, "TOP", new GdsHierarchyImportOptions());

        var pins = session.GetCellPins("soa", session.GetCellBBox("soa"));

        pins.Count.ShouldBe(3, "same-layer duplicates are both kept (the name normalizer handles naming)");
        pins.ShouldContain(p => p.Name == "o1");
        pins.ShouldContain(p => p.Name == "o1b");
        session.Infos.ShouldNotContain(i => i.Contains("coincident labels merged"),
            "nothing was dropped — no merge, no note");
    }

    [Fact]
    public async Task GetCellPins_LabelsBeyondMergeTolerance_NotMergedNoNote()
    {
        // "lc" sits 2 nm (0.002 µm > EdgeTouchToleranceUm) from "o1": beyond the
        // merge tolerance it is NOT a stacked label. It never becomes a pin
        // (its layer is not configured and a configured pin exists, so no
        // fallback) — the observable is the absent merge note: a stack wrongly
        // spanning 2 nm would report a dropped label.
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("soa", 0, 0)
            .EndCell()
            .BeginCell("soa")
                .Boundary(1, 0, (0, 1750), (10000, 1750), (10000, 2250), (0, 2250), (0, 1750))
                .Boundary(111, 0, (0, 0), (10000, 0), (10000, 4000), (0, 4000), (0, 0))
                .Text(1, 10, "o1", 0, 2000)
                .Text(99, 0, "lc", 2, 2000)
                .Text(1, 10, "i1", 10000, 2000)
            .EndCell()
            .EndLibrary()
            .ToArray());
        var session = new GdsHierarchyImportSession(library, "TOP", new GdsHierarchyImportOptions());

        var pins = session.GetCellPins("soa", session.GetCellBBox("soa"));

        pins.Count.ShouldBe(2, "labels 2 nm apart do not merge");
        pins[0].Name.ShouldBe("o1");
        pins[1].Name.ShouldBe("i1");
        session.Infos.ShouldNotContain(i => i.Contains("coincident labels merged"),
            "anchors beyond EdgeTouchToleranceUm form no stack — nothing was dropped");
    }

    [Fact]
    public async Task Explode_RouteNetworkWithDoubledIdenticalLabel_ConnectionRestored()
    {
        // The exporter wrote the "out" label twice, byte-identical (same layer,
        // text and anchor). Kept, the second copy would be renamed as a
        // duplicate pin at the same anchor: the route network between
        // dev#0.out and dev#1.in would count 3 touching pins — a junction, left
        // frozen with 0 connections. The verbatim copy must collapse silently.
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("dev", 0, 0)
                .SRef("dev", 20000, 0)
                .Boundary(1, 0, (10000, 1750), (20000, 1750), (20000, 2250), (10000, 2250), (10000, 1750))
            .EndCell()
            .BeginCell("dev")
                .Boundary(1, 0, (0, 1750), (10000, 1750), (10000, 2250), (0, 2250), (0, 1750))
                .Boundary(111, 0, (0, 0), (10000, 0), (10000, 4000), (0, 4000), (0, 0))
                .Text(235, 0, "in", 0, 2000)
                .Text(235, 0, "out", 10000, 2000)
                .Text(235, 0, "out", 10000, 2000)
            .EndCell()
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        var draft = result.ImportedCellDrafts.ShouldHaveSingleItem();
        draft.Pins.Count.ShouldBe(2, "the doubled identical label collapses into one pin");
        var connection = result.Connections.ShouldHaveSingleItem(
            "the 2-pin route network becomes a connection again, not a junction-frozen path");
        connection.IsRouteDerived.ShouldBeTrue();
        connection.A.PinName.ShouldBe("out");
        connection.B.PinName.ShouldBe("in");
        connection.XUm.ShouldBe(15.0, Tolerance);
        connection.YUm.ShouldBe(2.0, Tolerance);
        result.Warnings.ShouldNotContain(w => w.Contains("duplicate pin name"),
            "one physical pin must not trigger the duplicate-name rename warning");
        result.Infos.ShouldNotContain(i => i.Contains("coincident labels merged"),
            "verbatim-identical labels collapse silently — no different label was merged away");
        result.Infos.ShouldNotContain(i => i.Contains("junction"));
    }

    [Fact]
    public async Task Explode_RouteNetworkWithStackedHelperLabels_ConnectionRestored()
    {
        // Foundry shape: every pin anchor carries the real label on (235,0)
        // plus a stacked helper "lc" on (233,0). Without the dedup each device
        // reads as 4 pins and the route network between dev#0.out and dev#1.in
        // counts 4 touching pins — a junction, left frozen with 0 connections.
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("dev", 0, 0)
                .SRef("dev", 20000, 0)
                .Boundary(1, 0, (10000, 1750), (20000, 1750), (20000, 2250), (10000, 2250), (10000, 1750))
            .EndCell()
            .BeginCell("dev")
                .Boundary(1, 0, (0, 1750), (10000, 1750), (10000, 2250), (0, 2250), (0, 1750))
                .Boundary(111, 0, (0, 0), (10000, 0), (10000, 4000), (0, 4000), (0, 0))
                .Text(235, 0, "in", 0, 2000)
                .Text(233, 0, "lc", 0, 2000)
                .Text(235, 0, "out", 10000, 2000)
                .Text(233, 0, "lc", 10000, 2000)
            .EndCell()
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        var draft = result.ImportedCellDrafts.ShouldHaveSingleItem();
        draft.Pins.Count.ShouldBe(2, "each stacked anchor collapses into one pin");
        var connection = result.Connections.ShouldHaveSingleItem(
            "the 2-pin route network becomes a connection again, not a junction-frozen path");
        connection.IsRouteDerived.ShouldBeTrue();
        connection.A.PinName.ShouldBe("out");
        connection.B.PinName.ShouldBe("in");
        connection.XUm.ShouldBe(15.0, Tolerance);
        connection.YUm.ShouldBe(2.0, Tolerance);
        var note = result.Infos.Where(i => i.Contains("coincident labels merged")).ShouldHaveSingleItem(
            "one aggregated note for the cell (fired once although the cell is placed twice)");
        note.ShouldContain("coincident labels merged into one pin at 2 stacked anchors");
        result.Infos.ShouldNotContain(i => i.Contains("junction"));
    }

    [Fact]
    public async Task BlackBox_CoincidentLabels_MergedIntoOnePin()
    {
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("dev", 0, 0)
            .EndCell()
            .BeginCell("dev")
                .Boundary(1, 0, (0, 1750), (10000, 1750), (10000, 2250), (0, 2250), (0, 1750))
                .Boundary(111, 0, (0, 0), (10000, 0), (10000, 4000), (0, 4000), (0, 0))
                .Text(235, 0, "in", 0, 2000)
                .Text(233, 0, "lc", 0, 2000)
                .Text(235, 0, "out", 10000, 2000)
                .Text(233, 0, "lc", 10000, 2000)
            .EndCell()
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions
        {
            Mode = GdsHierarchyImportMode.BlackBox,
        });

        var draft = result.ImportedCellDrafts.ShouldHaveSingleItem();
        draft.Pins.Count.ShouldBe(2, "black-box pin detection dedups the same stacks");
        draft.Pins.ShouldContain(p => p.Name == "dev_in");
        draft.Pins.ShouldContain(p => p.Name == "dev_out");
        draft.Pins.ShouldNotContain(p => p.Name == "dev_lc");
        result.Infos.ShouldContain(i => i.Contains("coincident labels merged"));
    }
}
