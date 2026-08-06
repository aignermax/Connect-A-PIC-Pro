using CAP_DataAccess.Import.Gds;
using Shouldly;

namespace UnitTests.Import.Gds;

/// <summary>
/// Tests for the options validation of <see cref="GdsHierarchyImportOptions"/>
/// and <see cref="GdsPinDetectionOptions"/>: out-of-range tunables fail fast at
/// import/detection start instead of producing silently wrong geometry.
/// </summary>
public class GdsHierarchyImportOptionsTests
{
    private static async Task<GdsLibrary> MinimalLibrary() =>
        await new GdsReader().ReadAsync(new MemoryStream(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP").EndCell()
            .EndLibrary()
            .ToArray()));

    [Fact]
    public async Task ImportAsync_NegativeAbutmentTolerance_Throws()
    {
        var library = await MinimalLibrary();

        Should.Throw<ArgumentOutOfRangeException>(() =>
            GdsHierarchyImporter.ImportAsync(library, "TOP",
                new GdsHierarchyImportOptions { AbutmentToleranceUm = -0.1 }));
    }

    [Fact]
    public async Task ImportAsync_NegativeOutlineSimplificationTolerance_Throws()
    {
        var library = await MinimalLibrary();

        Should.Throw<ArgumentOutOfRangeException>(() =>
            GdsHierarchyImporter.ImportAsync(library, "TOP",
                new GdsHierarchyImportOptions { OutlineSimplificationToleranceUm = -0.1 }));
    }

    [Fact]
    public async Task ImportAsync_NegativeOutlinePointCap_Throws()
    {
        var library = await MinimalLibrary();

        Should.Throw<ArgumentOutOfRangeException>(() =>
            GdsHierarchyImporter.ImportAsync(library, "TOP",
                new GdsHierarchyImportOptions { MaxOutlinePointsPerCell = -1 }));
    }

    [Fact]
    public async Task ImportAsync_InvertedPinWidthWindow_Throws()
    {
        var library = await MinimalLibrary();

        var ex = Should.Throw<ArgumentException>(() =>
            GdsHierarchyImporter.ImportAsync(library, "TOP",
                new GdsHierarchyImportOptions
                {
                    PinDetection = new GdsPinDetectionOptions { MinPinWidthUm = 10, MaxPinWidthUm = 1 },
                }));
        ex.Message.ShouldContain("MinPinWidthUm");
    }

    [Fact]
    public async Task ImportAsync_NegativeEdgeTouchTolerance_Throws()
    {
        var library = await MinimalLibrary();

        Should.Throw<ArgumentOutOfRangeException>(() =>
            GdsHierarchyImporter.ImportAsync(library, "TOP",
                new GdsHierarchyImportOptions
                {
                    PinDetection = new GdsPinDetectionOptions { EdgeTouchToleranceUm = -0.001 },
                }));
    }

    [Fact]
    public void PinDetector_InvertedPinWidthWindow_Throws()
    {
        // Direct detector users get the same guard as the importer path.
        Should.Throw<ArgumentException>(() =>
            GdsPinDetector.Detect(
                new FlattenedGdsCell(),
                new GdsBoundingBox(0, 0, 10, 10),
                new GdsPinDetectionOptions { MinPinWidthUm = 10, MaxPinWidthUm = 1 }));
    }

    [Fact]
    public async Task ImportAsync_DefaultOptions_PassValidation()
    {
        var library = await MinimalLibrary();

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        result.TopCellName.ShouldBe("TOP");
    }
}
