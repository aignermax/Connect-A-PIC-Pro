using CAP_DataAccess.Import.Gds;
using Shouldly;

namespace UnitTests.Import.Gds;

/// <summary>
/// Fit-check against a real, externally-produced GDSII file shipped in
/// <c>Tools/gds-test-data/gdsfactory_mzi_like.gds</c>. The synthetic tests in
/// <c>GdsHierarchyImporterTests</c> verify the importer against hand-crafted
/// fixtures written by <see cref="GdsTestWriter"/>; these tests verify the same
/// code against a file actually produced by gdsfactory 9.47.0 (generic PDK) —
/// see <c>Tools/gds-test-data/README.md</c> for the generator script.
///
/// The file holds a small MZI-like circuit: mmi1x2 → bend_euler → straight
/// (arm 1) and mmi1x2 → straight (arm 2), abutted with gdsfactory's
/// <c>connect()</c> (nm-exact joints). Top cell <c>gdsfactory_mzi_like</c> has
/// 4 direct references (mmi1x2, bend_euler, 2× the same straight cell, one
/// rotated 90°). Every cell carries its port labels as TEXT on layer (1,10):
/// <c>o1</c>/<c>o2</c>/<c>o3</c> on the sub-cells and the circuit ports
/// <c>in0</c>/<c>out0</c>/<c>out1</c> on the top cell; waveguide cores are
/// polygons on (1,0). The tests parse the committed file with our own reader
/// only — no Python, no network.
/// </summary>
public class RealGdsFileIntegrationTests
{
    private static readonly string GdsPath = FindRepoRelative("Tools", "gds-test-data", "gdsfactory_mzi_like.gds");

    private const string TopCell = "gdsfactory_mzi_like";

    // ── Analyze ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListTopCellsAsync_FindsGdsfactoryTopCell()
    {
        File.Exists(GdsPath).ShouldBeTrue($"Reference file missing: {GdsPath}");

        var candidates = await new GdsImporter().ListTopCellsAsync(GdsPath);

        candidates.ShouldBe(new[] { TopCell });
    }

    [Fact]
    public async Task Reader_ParsesRealFileStructure_AndPortLabels()
    {
        await using var stream = File.OpenRead(GdsPath);
        var library = await new GdsReader().ReadAsync(stream);

        // 1 nm database unit, as gdsfactory writes it.
        library.DatabaseUnitsToMicrometers.ShouldBe(0.001, 1e-12);

        // Top + the three component cells (the straight is shared by two refs).
        library.Cells.Count.ShouldBe(4);
        library.TopCellCandidates.ShouldBe(new[] { TopCell });

        // Port labels are TEXT on (1,10) in every cell — the gdsfactory
        // port-label convention the pin detector defaults to.
        AssertLabelTexts(library, TopCell, "in0", "out0", "out1");
        AssertLabelTexts(library, "mmi1x2_", "o1", "o2", "o3");
        AssertLabelTexts(library, "bend_euler_", "o1", "o2");
        AssertLabelTexts(library, "straight_", "o1", "o2");

        // Waveguide cores on (1,0) in the three leaf cells.
        foreach (var prefix in new[] { "mmi1x2_", "bend_euler_", "straight_" })
        {
            var cell = FindCell(library, prefix);
            cell.Elements.OfType<GdsPolygon>().ShouldContain(p => p.Layer == 1 && p.DataType == 0);
        }
    }

    // ── Explode import ───────────────────────────────────────────────────────

    [Fact]
    public async Task Explode_ImportsFullHierarchy_WithLabelPinsAndReconstructedConnections()
    {
        var library = await ReadLibraryAsync();

        var result = await GdsHierarchyImporter.ImportAsync(library, TopCell, new GdsHierarchyImportOptions());

        result.Mode.ShouldBe(GdsHierarchyImportMode.ExplodeHierarchy);
        result.TopCellName.ShouldBe(TopCell);

        // 4 direct references in the file → 4 instances; 3 distinct referenced
        // cells → 3 drafts (the straight serves two instances).
        result.Instances.Count.ShouldBe(4);
        result.ImportedCellDrafts.Count.ShouldBe(3);

        // Every instance is unknown to the PDK (no resolver configured) and
        // therefore backed by an imported draft.
        result.Instances.ShouldAllBe(i => i.KnownComponentIdentifier == null && i.CellDraftName != null);

        // The two straight instances share one draft; one of them is the
        // 90°-rotated arm (GDS +90° ≡ app −90° ≡ 270°).
        var straightDraft = result.ImportedCellDrafts.Single(d => d.CellName.StartsWith("straight_"));
        var straightInstances = result.Instances.Where(i => i.CellDraftName == straightDraft.CellName).ToList();
        straightInstances.Count.ShouldBe(2);
        straightInstances.Count(i => i.RotationDegrees == 0).ShouldBe(1);
        straightInstances.Count(i => i.RotationDegrees == 270).ShouldBe(1);
        result.Instances.ShouldAllBe(i => !i.Reflected);

        // Every draft resolves its pins from the file's (1,10) port LABELS —
        // the whole point of the file's label convention.
        AssertLabelPins(result, "mmi1x2_", "o1", "o2", "o3");
        AssertLabelPins(result, "bend_euler_", "o1", "o2");
        AssertLabelPins(result, "straight_", "o1", "o2");

        // Connections: the three connect() joints are nm-exact, so all three
        // instance-to-instance abutments reconstruct, and the three circuit
        // ports on the top cell pair with the remaining free instance pins.
        result.Connections.Count.ShouldBe(6);

        var abutments = result.Connections.Where(c => !c.B.IsTopLevelPort).ToList();
        abutments.Count.ShouldBe(3);
        // mmi.o2 ↔ bend.o1, bend.o2 ↔ straight.o1, mmi.o3 ↔ straight.o1.
        abutments.Count(c => PinPairIs(c, "o2", "o1")).ShouldBe(2);
        abutments.ShouldContain(c => PinPairIs(c, "o3", "o1"));

        var external = result.Connections.Where(c => c.B.IsTopLevelPort).ToList();
        external.Count.ShouldBe(3);
        external.Select(c => c.B.PinName).OrderBy(n => n).ShouldBe(new[] { "in0", "out0", "out1" });
        external.ShouldAllBe(c => c.A.PinName == "o1" || c.A.PinName == "o2");

        // A real gdsfactory file with nm-exact abutments and cardinal
        // rotations must import cleanly.
        result.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task Explode_Drafts_HaveSaneGeometry()
    {
        var library = await ReadLibraryAsync();

        var result = await GdsHierarchyImporter.ImportAsync(library, TopCell, new GdsHierarchyImportOptions());

        result.BoundingBox.MaxX.ShouldBeGreaterThan(0);
        result.BoundingBox.MaxY.ShouldBeGreaterThan(0);

        foreach (var draft in result.ImportedCellDrafts)
        {
            draft.WidthUm.ShouldBeGreaterThan(0, $"{draft.CellName}: zero width");
            draft.HeightUm.ShouldBeGreaterThan(0, $"{draft.CellName}: zero height");
            AssertPinsWithinDraftBounds(draft);

            // All three cells carry waveguide polygons on (1,0), so the
            // flattened outlines must be non-empty.
            draft.Outlines.ShouldNotBeEmpty($"{draft.CellName}: no outlines");
            draft.Outlines.ShouldContain(p => p.Layer == 1 && p.DataType == 0);

            // RawCode round-trip snippet re-loads this cell from the source file.
            draft.RawCodeBackend.ShouldBe("nazca");
            draft.RawCode.ShouldContain("nd.load_gds(");
        }
    }

    // ── Black-box import ─────────────────────────────────────────────────────

    [Fact]
    public async Task BlackBox_TopCell_BecomesSingleDraft_WithCircuitPortPins()
    {
        var library = await ReadLibraryAsync();

        var result = await GdsHierarchyImporter.ImportAsync(
            library, TopCell, new GdsHierarchyImportOptions { Mode = GdsHierarchyImportMode.BlackBox });

        result.Mode.ShouldBe(GdsHierarchyImportMode.BlackBox);
        result.Instances.ShouldBeEmpty();
        result.Connections.ShouldBeEmpty();

        var draft = result.ImportedCellDrafts.ShouldHaveSingleItem();
        draft.CellName.ShouldBe(TopCell);
        draft.WidthUm.ShouldBeGreaterThan(0);
        draft.HeightUm.ShouldBeGreaterThan(0);
        AssertPinsWithinDraftBounds(draft);

        // The circuit ports written on the top cell become label pins.
        draft.Pins.Count.ShouldBeGreaterThan(0);
        var labelPinNames = draft.Pins.Where(p => p.Source == DetectedPinSource.Label).Select(p => p.Name).OrderBy(n => n);
        labelPinNames.ShouldBe(new[] { "in0", "out0", "out1" });

        // The flattened hierarchy (three WG-carrying children) yields outlines.
        draft.Outlines.ShouldNotBeEmpty();
        draft.Outlines.ShouldContain(p => p.Layer == 1 && p.DataType == 0);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<GdsLibrary> ReadLibraryAsync()
    {
        File.Exists(GdsPath).ShouldBeTrue($"Reference file missing: {GdsPath}");
        await using var stream = File.OpenRead(GdsPath);
        return await new GdsReader().ReadAsync(stream);
    }

    private static GdsCell FindCell(GdsLibrary library, string namePrefix) =>
        library.Cells.Values.Single(c => c.Name.StartsWith(namePrefix, StringComparison.Ordinal));

    private static void AssertLabelTexts(GdsLibrary library, string cellNamePrefix, params string[] expected)
    {
        var cell = FindCell(library, cellNamePrefix);
        var texts = cell.Elements.OfType<GdsText>()
            .Where(t => t.Layer == 1 && t.TextType == 10)
            .Select(t => t.Text)
            .OrderBy(t => t, StringComparer.Ordinal);
        texts.ShouldBe(expected.OrderBy(t => t, StringComparer.Ordinal), $"cell {cell.Name}: (1,10) port labels");
    }

    private static void AssertLabelPins(GdsCircuitImport result, string draftNamePrefix, params string[] expected)
    {
        var draft = result.ImportedCellDrafts.Single(d => d.CellName.StartsWith(draftNamePrefix, StringComparison.Ordinal));
        var labelPins = draft.Pins.Where(p => p.Source == DetectedPinSource.Label).Select(p => p.Name).ToList();
        foreach (var name in expected)
            labelPins.ShouldContain(name, $"draft {draft.CellName}: label pin '{name}' missing");
    }

    private static bool PinPairIs(GdsPinPair pair, string a, string b) =>
        (pair.A.PinName == a && pair.B.PinName == b) || (pair.A.PinName == b && pair.B.PinName == a);

    /// <summary>The PdkLoader rule: pins within [0, W] × [0, H] (±1 µm tolerance).</summary>
    private static void AssertPinsWithinDraftBounds(GdsCellDraft draft)
    {
        foreach (var pin in draft.Pins)
        {
            pin.XUm.ShouldBeGreaterThanOrEqualTo(-1.0, $"{draft.CellName}.{pin.Name}: X out of bounds");
            pin.XUm.ShouldBeLessThanOrEqualTo(draft.WidthUm + 1.0, $"{draft.CellName}.{pin.Name}: X out of bounds");
            pin.YUm.ShouldBeGreaterThanOrEqualTo(-1.0, $"{draft.CellName}.{pin.Name}: Y out of bounds");
            pin.YUm.ShouldBeLessThanOrEqualTo(draft.HeightUm + 1.0, $"{draft.CellName}.{pin.Name}: Y out of bounds");
        }
    }

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
