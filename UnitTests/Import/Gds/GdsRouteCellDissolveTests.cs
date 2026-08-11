using CAP_DataAccess.Import.Gds;
using Shouldly;

namespace UnitTests.Import.Gds;

/// <summary>
/// Regression tests for route-cell dissolution (<c>GdsRouteCellDissolver</c>):
/// re-importing an export whose routed interconnects are REFERENCED cells must
/// restore the connection through the route matcher instead of creating a
/// bogus component draft. Fixtures: 1 db unit = 1 nm (<see cref="GdsTestWriter"/>).
/// </summary>
public class GdsRouteCellDissolveTests
{
    [Fact]
    public async Task Explode_RouteCellBetweenTwoDevices_RestoresConnectionWithoutDraft()
    {
        // wgA.out at GDS (10, 2), wgB.in at GDS (20, 2); the label-free route
        // cell "waveguide" spans exactly the gap on the waveguide layer (1,0).
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("wgA", 0, 0)
                .SRef("waveguide", 10000, 0)
                .SRef("wgB", 20000, 0)
            .EndCell()
            .DeviceCell("wgA")
            .DeviceCell("wgB")
            .RouteStripCell("waveguide")
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        // No "waveguide" component draft and no placed route instance.
        result.ImportedCellDrafts.Select(d => d.CellName).ShouldBe(new[] { "wgA", "wgB" });
        result.Instances.Count.ShouldBe(2);
        result.Instances.ShouldAllBe(i => i.CellName != "waveguide");

        // The dissolved geometry reconstructs the connection between the pins.
        var connection = result.Connections.ShouldHaveSingleItem();
        EndpointNames(result, connection).ShouldBe(new[] { "wgA#0.out", "wgB#0.in" }, ignoreOrder: true);

        // The route polygon was consumed by the matcher — nothing froze.
        result.TopCellWaveguidePolygons.ShouldBeEmpty();
        result.Warnings.ShouldBeEmpty();
        result.Infos.ShouldContain(i =>
            i.Contains("Route cell 'waveguide'") && i.Contains("1 instance(s)") && i.Contains("dissolved"));
        result.Infos.ShouldContain(i => i.Contains("restored as 1 real connection(s)"));
    }

    [Fact]
    public async Task Explode_RotatedRouteCellInstance_StillRestoresConnection()
    {
        // The same strip placed rotated 180° at (20, 4): its transformed span is
        // again GDS x 10..20 at y ≈ 2, so the dissolution transform math must
        // land it on both pins.
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("wgA", 0, 0)
                .SRef("straight_7", 20000, 4000, angleDegrees: 180)
                .SRef("wgB", 20000, 0)
            .EndCell()
            .DeviceCell("wgA")
            .DeviceCell("wgB")
            .RouteStripCell("straight_7")
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        result.ImportedCellDrafts.Select(d => d.CellName).ShouldBe(new[] { "wgA", "wgB" });
        var connection = result.Connections.ShouldHaveSingleItem();
        EndpointNames(result, connection).ShouldBe(new[] { "wgA#0.out", "wgB#0.in" }, ignoreOrder: true);
        result.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task Explode_MetalRouteCell_RestoresElectricalConnection()
    {
        // The route cell's strip lives on the metal layer (11,0): the metal
        // matcher restores it as an ELECTRICAL connection and the touched draft
        // pins are inferred electrical.
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("wgA", 0, 0)
                .SRef("waveguide_metal", 10000, 0)
                .SRef("wgB", 20000, 0)
            .EndCell()
            .DeviceCell("wgA")
            .DeviceCell("wgB")
            .RouteStripCell("waveguide_metal", layer: 11)
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        result.ImportedCellDrafts.Select(d => d.CellName).ShouldBe(new[] { "wgA", "wgB" });
        var connection = result.Connections.ShouldHaveSingleItem();
        EndpointNames(result, connection).ShouldBe(new[] { "wgA#0.out", "wgB#0.in" }, ignoreOrder: true);

        result.ImportedCellDrafts[0].Pins.Single(p => p.Name == "out").IsElectrical.ShouldBe(true);
        result.ImportedCellDrafts[1].Pins.Single(p => p.Name == "in").IsElectrical.ShouldBe(true);
        result.Infos.ShouldContain(i => i.Contains("1 electrical connection(s)"));
    }

    [Fact]
    public async Task Explode_DanglingRouteCell_DissolvesToFrozenPathWithoutDraft()
    {
        // A route cell touching no pins still dissolves (no draft), but its
        // polygon cannot pair two pins — it degrades to a frozen route path.
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("wgA", 0, 0)
                .SRef("waveguide", 0, 20000)
            .EndCell()
            .DeviceCell("wgA")
            .RouteStripCell("waveguide")
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        result.ImportedCellDrafts.Select(d => d.CellName).ShouldBe(new[] { "wgA" });
        result.Connections.ShouldBeEmpty();
        result.TopCellWaveguidePolygons.ShouldHaveSingleItem();
        result.Infos.ShouldContain(i => i.Contains("Route cell 'waveguide'") && i.Contains("dissolved"));
    }

    [Fact]
    public async Task Explode_LabeledCellWithRouteName_IsNotDissolved()
    {
        // A label anywhere marks a device cell: route-style name and pure
        // route-layer geometry notwithstanding, it must stay a component draft.
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("straight_1", 0, 0)
            .EndCell()
            .BeginCell("straight_1")
                .Boundary(1, 0, (0, 1750), (10000, 1750), (10000, 2250), (0, 2250), (0, 1750))
                .Text(1, 10, "o1", 0, 2000)
                .Text(1, 10, "o2", 10000, 2000)
            .EndCell()
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        result.ImportedCellDrafts.ShouldHaveSingleItem().CellName.ShouldBe("straight_1");
        result.Instances.ShouldHaveSingleItem().CellName.ShouldBe("straight_1");
        result.Infos.ShouldNotContain(i => i.Contains("dissolved"));
    }

    [Fact]
    public async Task Explode_RouteLayerCellWithNonRouteName_IsNotDissolved()
    {
        // Pure route-layer geometry alone must not dissolve: gdsfactory stub
        // cells ("stub_*") are label-free (1,0) rectangles and remain components.
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("stub_thing", 0, 0)
            .EndCell()
            .RouteStripCell("stub_thing")
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        result.ImportedCellDrafts.ShouldHaveSingleItem().CellName.ShouldBe("stub_thing");
        result.Instances.ShouldHaveSingleItem().CellName.ShouldBe("stub_thing");
        result.Infos.ShouldNotContain(i => i.Contains("dissolved"));
    }

    [Fact]
    public async Task Explode_KnownResolvedRouteNamedCell_IsNotDissolved()
    {
        // A deliberate known-component binding wins over dissolution.
        var known = new KnownComponent(
            "wg_lib", "testpdk", 10, 0.5,
            new[]
            {
                new DetectedPin { Name = "in", XUm = 0, YUm = 0.25, AngleDegrees = 180, Source = DetectedPinSource.Label },
                new DetectedPin { Name = "out", XUm = 10, YUm = 0.25, AngleDegrees = 0, Source = DetectedPinSource.Label },
            });

        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("waveguide", 0, 0)
            .EndCell()
            .RouteStripCell("waveguide")
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(
            library, "TOP", new GdsHierarchyImportOptions
            {
                ResolveKnownComponent = name => name == "waveguide" ? known : null,
            });

        result.ImportedCellDrafts.ShouldBeEmpty();
        var instance = result.Instances.ShouldHaveSingleItem();
        instance.KnownComponentIdentifier.ShouldBe("wg_lib");
        result.Infos.ShouldNotContain(i => i.Contains("dissolved"));
    }

    // ── Fixture helpers ──────────────────────────────────────────────────────

    private static async Task<GdsLibrary> ReadLibraryAsync(byte[] gds) =>
        await new GdsReader().ReadAsync(new MemoryStream(gds));

    /// <summary>Both endpoints of a pair as "instance.pin" strings.</summary>
    private static string[] EndpointNames(GdsCircuitImport result, GdsPinPair pair) =>
        new[] { pair.A, pair.B }
            .Select(e => $"{result.Instances[e.InstanceIndex].InstanceName}.{e.PinName}")
            .ToArray();
}

/// <summary>GDS fixture cell builders for the route-cell dissolution tests.</summary>
file static class GdsRouteCellDissolveTestCells
{
    /// <summary>
    /// 10×4 µm device cell: 0.5 µm core stripe on (1,0), extent rectangle on
    /// the non-route layer (111,0), in/out port labels on (1,10) at y = 2 µm.
    /// </summary>
    public static GdsTestWriter DeviceCell(this GdsTestWriter writer, string name) =>
        writer
            .BeginCell(name)
                .Boundary(1, 0, (0, 1750), (10000, 1750), (10000, 2250), (0, 2250), (0, 1750))
                .Boundary(111, 0, (0, 0), (10000, 0), (10000, 4000), (0, 4000), (0, 0))
                .Text(1, 10, "in", 0, 2000)
                .Text(1, 10, "out", 10000, 2000)
            .EndCell();

    /// <summary>
    /// Label-free routed-interconnect cell: a single 10 × 0.5 µm strip
    /// (y ∈ [1.75, 2.25]) on the given route layer — the shape our exporters
    /// emit for an instantiated route segment.
    /// </summary>
    public static GdsTestWriter RouteStripCell(this GdsTestWriter writer, string name, int layer = 1) =>
        writer
            .BeginCell(name)
                .Boundary(layer, 0, (0, 1750), (10000, 1750), (10000, 2250), (0, 2250), (0, 1750))
            .EndCell();
}
