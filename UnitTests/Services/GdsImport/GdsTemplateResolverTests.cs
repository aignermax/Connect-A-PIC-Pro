using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.ViewModels.Library;
using CAP_DataAccess.Import.Gds;
using Shouldly;
using UnitTests.Import.Gds;
using Xunit;

namespace UnitTests.Services.GdsImport;

/// <summary>
/// Tests for <see cref="GdsTemplateResolver"/>: known-component resolution from
/// the loaded component library, including the hash-suffix-stripped retry the
/// hierarchy importer performs with this resolver.
/// </summary>
public class GdsTemplateResolverTests
{
    private const double Tolerance = 1e-9;

    private static ComponentTemplate MmiTemplate() => new()
    {
        Name = "mmi1x2",
        PdkSource = "testpdk",
        WidthMicrometers = 30,
        HeightMicrometers = 10,
        PinDefinitions = new[]
        {
            new PinDefinition("o1", 0, 5, 180),
            new PinDefinition("o2", 30, 5, 0),
        },
    };

    [Fact]
    public void Resolver_ExactTemplateName_ResolvesWithTemplatePinsInAppSpace()
    {
        var resolver = GdsTemplateResolver.BuildKnownComponentResolver(new[] { MmiTemplate() });

        var known = resolver("mmi1x2");

        known.ShouldNotBeNull();
        known.Identifier.ShouldBe("mmi1x2");
        known.PdkSource.ShouldBe("testpdk");
        known.WidthUm.ShouldBe(30, Tolerance);
        known.HeightUm.ShouldBe(10, Tolerance);
        known.Pins.Count.ShouldBe(2);
        known.Pins[0].Name.ShouldBe("o1");
        known.Pins[0].XUm.ShouldBe(0, Tolerance);
        known.Pins[0].YUm.ShouldBe(5, Tolerance);
        known.Pins[0].AngleDegrees.ShouldBe(180, Tolerance);
        known.Pins[1].Name.ShouldBe("o2");
        known.Pins[1].XUm.ShouldBe(30, Tolerance);
    }

    [Fact]
    public void Resolver_UnknownCellName_ReturnsNull()
    {
        var resolver = GdsTemplateResolver.BuildKnownComponentResolver(new[] { MmiTemplate() });

        resolver("does_not_exist").ShouldBeNull();
    }

    [Fact]
    public async Task Resolver_HashSuffixedCellName_ResolvesToBaseTemplate()
    {
        // The importer retries hash-stripped candidates with this resolver:
        // "mmi1x2_A1B2C3" → "mmi1x2" hits the template.
        var templates = new[] { MmiTemplate() };
        var resolver = GdsTemplateResolver.BuildKnownComponentResolver(templates);

        var library = await new GdsReader().ReadAsync(new MemoryStream(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("mmi1x2_A1B2C3", 0, 0)
            .EndCell()
            .BeginCell("mmi1x2_A1B2C3")
                .Boundary(1, 0, (0, 0), (30000, 0), (30000, 10000), (0, 10000), (0, 0))
            .EndCell()
            .EndLibrary()
            .ToArray()));

        var result = await GdsHierarchyImporter.ImportAsync(
            library, "TOP", new GdsHierarchyImportOptions { ResolveKnownComponent = resolver });

        result.ImportedCellDrafts.ShouldBeEmpty("the cell resolved to a known component — no draft");
        var instance = result.Instances.ShouldHaveSingleItem();
        instance.KnownComponentIdentifier.ShouldBe("mmi1x2");
        instance.PdkSource.ShouldBe("testpdk");
        instance.CellDraftName.ShouldBeNull();
    }

    [Fact]
    public async Task Resolver_UnknownCell_BecomesDraft()
    {
        var templates = new[] { MmiTemplate() };
        var resolver = GdsTemplateResolver.BuildKnownComponentResolver(templates);

        var library = await new GdsReader().ReadAsync(new MemoryStream(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("other_cell", 0, 0)
            .EndCell()
            .BeginCell("other_cell")
                .Boundary(1, 0, (0, 0), (10000, 0), (10000, 4000), (0, 4000), (0, 0))
            .EndCell()
            .EndLibrary()
            .ToArray()));

        var result = await GdsHierarchyImporter.ImportAsync(
            library, "TOP", new GdsHierarchyImportOptions { ResolveKnownComponent = resolver });

        result.ImportedCellDrafts.ShouldHaveSingleItem().CellName.ShouldBe("other_cell");
        result.Instances.ShouldHaveSingleItem().CellDraftName.ShouldBe("other_cell");
    }
}
