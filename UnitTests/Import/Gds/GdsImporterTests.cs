using CAP_DataAccess.Import.Gds;
using Shouldly;

namespace UnitTests.Import.Gds;

/// <summary>
/// End-to-end tests for <see cref="GdsImporter"/> against temporary files —
/// the importer's file handling is the only part that needs a real file system.
/// </summary>
public class GdsImporterTests
{
    [Fact]
    public async Task ImportAsync_ReturnsResolvedResult()
    {
        var gds = GdsTestWriter.Create()
            .StandardPrologue("e2e")
            .BeginCell("TOP")
                .Boundary(1, 0, (0, 0), (2000, 0), (2000, 1000), (0, 0))
                .SRef("CHILD", 5000, 5000)
                .Text(1, 0, "PORT1", 500, 500)
            .EndCell()
            .BeginCell("CHILD")
                .Boundary(2, 0, (0, 0), (1000, 1000), (0, 0))
            .EndCell()
            .EndLibrary()
            .ToArray();

        using var tmp = WriteTempFile(gds);
        var result = await new GdsImporter().ImportAsync(tmp.Path, "TOP");

        result.LibraryName.ShouldBe("e2e");
        result.CellNames.ShouldBe(new[] { "TOP", "CHILD" });
        result.TopCellCandidates.ShouldBe(new[] { "TOP" });
        result.TopCellName.ShouldBe("TOP");
        result.Polygons.Count.ShouldBe(2);
        result.Texts.ShouldHaveSingleItem().Text.ShouldBe("PORT1");
        result.BoundingBox.MinX.ShouldBe(0);
        result.BoundingBox.MinY.ShouldBe(0);
        result.BoundingBox.MaxX.ShouldBe(6);
        result.BoundingBox.MaxY.ShouldBe(6);
        var instance = result.Instances.ShouldHaveSingleItem();
        instance.CellName.ShouldBe("CHILD");
        instance.Offset.ShouldBe(new GdsPoint(5, 5));
    }

    [Fact]
    public async Task ListTopCellsAsync_ReturnsUnreferencedCells()
    {
        var gds = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("CHILD", 0, 0)
            .EndCell()
            .BeginCell("CHILD").EndCell()
            .EndLibrary()
            .ToArray();

        using var tmp = WriteTempFile(gds);
        var candidates = await new GdsImporter().ListTopCellsAsync(tmp.Path);

        candidates.ShouldBe(new[] { "TOP" });
    }

    [Fact]
    public async Task ImportAsync_MissingFile_Throws()
    {
        await Should.ThrowAsync<FileNotFoundException>(() =>
            new GdsImporter().ImportAsync("/nonexistent/file.gds", "TOP"));
    }

    [Fact]
    public async Task ImportAsync_CorruptFile_ThrowsInvalidData()
    {
        using var tmp = WriteTempFile(new byte[] { 0x00, 0x06, 0x00, 0x02, 0x00 }); // truncated HEADER record

        await Should.ThrowAsync<InvalidDataException>(() =>
            new GdsImporter().ImportAsync(tmp.Path, "TOP"));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Writes a temp file whose wrapper deletes it at end-of-scope.</summary>
    private static TempFile WriteTempFile(byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"gds_test_{Guid.NewGuid()}.gds");
        File.WriteAllBytes(path, content);
        return new TempFile(path);
    }

    private sealed class TempFile : IDisposable
    {
        public string Path { get; }
        public TempFile(string path) => Path = path;
        public void Dispose()
        {
            if (File.Exists(Path)) File.Delete(Path);
        }
    }
}
