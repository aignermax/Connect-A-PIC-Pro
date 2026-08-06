using CAP_DataAccess.Import.Gds;
using Shouldly;

namespace UnitTests.Import.Gds;

/// <summary>
/// End-to-end tests for the file-based read+flatten path
/// (<see cref="GdsReader"/> + <see cref="GdsCellFlattener"/>) against temporary
/// files — file handling is the only part that needs a real file system.
/// </summary>
public class GdsFileImportTests
{
    [Fact]
    public async Task ReadAndFlatten_ReturnsResolvedResult()
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
        var library = await ReadLibraryAsync(tmp.Path);
        var flattener = new GdsCellFlattener(library);
        var flattened = flattener.Flatten("TOP");

        library.Name.ShouldBe("e2e");
        library.Cells.Keys.ShouldBe(new[] { "TOP", "CHILD" });
        library.TopCellCandidates.ShouldBe(new[] { "TOP" });
        flattened.Polygons.Count.ShouldBe(2);
        flattened.Texts.ShouldHaveSingleItem().Text.ShouldBe("PORT1");
        var boundingBox = flattener.GetBoundingBox("TOP");
        boundingBox.MinX.ShouldBe(0);
        boundingBox.MinY.ShouldBe(0);
        boundingBox.MaxX.ShouldBe(6);
        boundingBox.MaxY.ShouldBe(6);
        var instance = flattener.GetInstanceTree("TOP").ShouldHaveSingleItem();
        instance.CellName.ShouldBe("CHILD");
        instance.Offset.ShouldBe(new GdsPoint(5, 5));
    }

    [Fact]
    public async Task Read_ListsUnreferencedCellsAsTopCandidates()
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
        var library = await ReadLibraryAsync(tmp.Path);

        library.TopCellCandidates.ShouldBe(new[] { "TOP" });
    }

    [Fact]
    public async Task Read_MissingFile_Throws()
    {
        await Should.ThrowAsync<FileNotFoundException>(() =>
            ReadLibraryAsync(Path.Combine(Path.GetTempPath(), "nonexistent_gds_test_file.gds")));
    }

    [Fact]
    public async Task Read_CorruptFile_ThrowsInvalidData()
    {
        using var tmp = WriteTempFile(new byte[] { 0x00, 0x06, 0x00, 0x02, 0x00 }); // truncated HEADER record

        await Should.ThrowAsync<InvalidDataException>(() => ReadLibraryAsync(tmp.Path));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Opens the file the same way production does and parses it.</summary>
    private static async Task<GdsLibrary> ReadLibraryAsync(string path)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);
        return await new GdsReader().ReadAsync(stream);
    }

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
