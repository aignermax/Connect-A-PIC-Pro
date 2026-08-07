using CAP_DataAccess.Import.Gds;
using Shouldly;

namespace UnitTests.Import.Gds;

/// <summary>
/// Regression tests: our exporter's metal layer numbers
/// ((11,0)/(12,0), <c>MetalRoutingSpec</c> defaults) must not be treated as
/// universal truth. Foundry layer tables assign those numbers differently —
/// the field-test file carried OPTICAL routes on (12, 0), and the hardcoded
/// metal default turned every reconstructed connection electrical (right-angle
/// metal routing, frozen above the reroute cap). The Lunima defaults now apply
/// only when the file carries our export sentinel
/// (<see cref="GdsOwnExportSentinel"/>); foreign files need an explicit layer
/// mapping.
/// </summary>
public class GdsForeignLayerMapTests
{
    private const double Tolerance = 1e-6;

    /// <summary>
    /// The issue's core scenario: a foreign file whose (12, 0) carries optical
    /// routes. With the user's mapping ((12, 0) as an optical route layer) the
    /// bridging polygon between wgA.out (10, 2) and wgB.in (15, 2) must come
    /// back as an OPTICAL route-derived connection, not a metal trace.
    /// </summary>
    [Fact]
    public async Task ForeignFile_OpticalRoutesOnLayer12_ImportOpticalWhenMappedAsRoute()
    {
        var library = await ReadLibraryAsync(ForeignBridgeFixture(bridgeLayer: 12));

        var result = await GdsHierarchyImporter.ImportAsync(
            library, "TOP", new GdsHierarchyImportOptions { RouteLayers = [(12, 0)] });

        var connection = result.Connections.ShouldHaveSingleItem();
        connection.IsRouteDerived.ShouldBeTrue();
        connection.IsElectrical.ShouldBeFalse(
            "(12,0) is an optical route layer in this file's mapping");
        connection.A.PinName.ShouldBe("out");
        connection.B.PinName.ShouldBe("in");
        connection.XUm.ShouldBe(12.5, Tolerance);
        connection.YUm.ShouldBe(2.0, Tolerance);

        // No pin may be inferred electrical from the (12, 0) touch.
        result.ImportedCellDrafts
            .SelectMany(draft => draft.Pins)
            .ShouldAllBe(pin => pin.IsElectrical == null);
        result.Warnings.ShouldBeEmpty();
    }

    /// <summary>
    /// A foreign file WITHOUT any mapping: the Lunima metal defaults must NOT
    /// kick in — the (12, 0) polygon stays render-only background geometry, no
    /// electrical connection is fabricated, and one info note tells the user
    /// how to supply the mapping.
    /// </summary>
    [Fact]
    public async Task ForeignFile_DefaultOptions_DoesNotTreatLayer12AsMetal()
    {
        var library = await ReadLibraryAsync(ForeignBridgeFixture(bridgeLayer: 12));

        var result = await GdsHierarchyImporter.ImportAsync(
            library, "TOP", new GdsHierarchyImportOptions());

        result.Connections.ShouldBeEmpty(
            "no metal default may match a foreign file's (12,0) polygons");
        var residual = result.TopCellResidualPolygons.ShouldHaveSingleItem();
        residual.Layer.ShouldBe(12);
        result.Infos.ShouldContain(
            i => i.Contains("no Lunima export marker") && i.Contains("metal layers"));
    }

    /// <summary>
    /// Our OWN exports keep working without configuration: the export sentinel
    /// (a <c>ConnectAPIC_</c>-prefixed cell) re-enables the exporter metal
    /// defaults, so an (11, 0) trace still reconstructs as an ELECTRICAL
    /// connection out of the box.
    /// </summary>
    [Fact]
    public async Task OwnExport_MetalDefaultsStillApply()
    {
        var library = await ReadLibraryAsync(ForeignBridgeFixture(
            bridgeLayer: 11, includeOwnExportSentinelCell: true));

        var result = await GdsHierarchyImporter.ImportAsync(
            library, "TOP", new GdsHierarchyImportOptions());

        var connection = result.Connections.ShouldHaveSingleItem();
        connection.IsRouteDerived.ShouldBeTrue();
        connection.IsElectrical.ShouldBeTrue("Lunima exports flatten metal traces onto (11,0)");
        result.Infos.ShouldNotContain(i => i.Contains("no Lunima export marker"));
    }

    /// <summary>An explicit metal mapping on a foreign file always wins over AUTO.</summary>
    [Fact]
    public async Task ForeignFile_ExplicitMetalMapping_ImportsElectrical()
    {
        var library = await ReadLibraryAsync(ForeignBridgeFixture(bridgeLayer: 40));

        var result = await GdsHierarchyImporter.ImportAsync(
            library, "TOP", new GdsHierarchyImportOptions { MetalRouteLayers = [(40, 0)] });

        var connection = result.Connections.ShouldHaveSingleItem();
        connection.IsElectrical.ShouldBeTrue("the user mapped (40,0) as metal");
        result.Infos.ShouldNotContain(
            i => i.Contains("no Lunima export marker"),
            "the note only fires when the metal layers were left on AUTO");
    }

    [Fact]
    public async Task Sentinel_RecognizesOnlyConnectApicPrefixedCells()
    {
        var foreign = await ReadLibraryAsync(ForeignBridgeFixture(bridgeLayer: 12));
        var own = await ReadLibraryAsync(ForeignBridgeFixture(
            bridgeLayer: 12, includeOwnExportSentinelCell: true));

        GdsOwnExportSentinel.IsOwnExport(foreign).ShouldBeFalse();
        GdsOwnExportSentinel.IsOwnExport(own).ShouldBeTrue();
    }

    // ── Fixture ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Two 10×4 µm waveguide cells 5 µm apart plus a top-cell-own bridge
    /// polygon on <paramref name="bridgeLayer"/> running exactly through
    /// wgA.out (10, 2) and wgB.in (15, 2) — the same geometry as the importer's
    /// route-derivation fixtures. With
    /// <paramref name="includeOwnExportSentinelCell"/> an unreferenced
    /// <c>ConnectAPIC_Design</c> cell marks the file as a Lunima export.
    /// </summary>
    private static byte[] ForeignBridgeFixture(
        int bridgeLayer, bool includeOwnExportSentinelCell = false)
    {
        var writer = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("wgA", 0, 0)
                .SRef("wgB", 15000, 0)
                .Boundary(bridgeLayer, 0,
                    (10000, 1750), (15000, 1750), (15000, 2250), (10000, 2250), (10000, 1750))
            .EndCell()
            .WaveguideCell("wgA")
            .WaveguideCell("wgB");
        if (includeOwnExportSentinelCell)
            writer = writer.BeginCell("ConnectAPIC_Design").EndCell();
        return writer.EndLibrary().ToArray();
    }

    private static async Task<GdsLibrary> ReadLibraryAsync(byte[] gds) =>
        await new GdsReader().ReadAsync(new MemoryStream(gds));
}

/// <summary>GDS fixture cell builder local to these tests (mirrors the importer tests' cell).</summary>
file static class GdsForeignLayerMapTestCells
{
    /// <summary>
    /// 10×4 µm cell like a real gdsfactory waveguide: 0.5 µm core stripe on
    /// (1,0), extent rectangle on the non-waveguide layer (111,0), in/out port
    /// labels on (1,10) at (0, 2) / (10, 2).
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
