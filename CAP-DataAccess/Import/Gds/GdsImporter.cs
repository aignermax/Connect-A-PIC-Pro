namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// Default <see cref="IGdsImporter"/> implementation: wires the streaming
/// <see cref="GdsReader"/> to the <see cref="GdsCellFlattener"/>.
/// </summary>
public sealed class GdsImporter : IGdsImporter
{
    /// <inheritdoc/>
    public async Task<GdsImportResult> ImportAsync(string gdsPath, string topCellName, CancellationToken ct = default)
    {
        var library = await ReadLibraryAsync(gdsPath, ct).ConfigureAwait(false);
        var flattener = new GdsCellFlattener(library);
        var flattened = flattener.Flatten(topCellName);

        return new GdsImportResult
        {
            LibraryName = library.Name,
            CellNames = library.Cells.Keys.ToList(),
            TopCellCandidates = library.TopCellCandidates,
            TopCellName = topCellName,
            BoundingBox = flattener.GetBoundingBox(topCellName),
            Polygons = flattened.Polygons,
            Texts = flattened.Texts,
            Instances = flattener.GetInstanceTree(topCellName),
        };
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> ListTopCellsAsync(string gdsPath, CancellationToken ct = default)
    {
        var library = await ReadLibraryAsync(gdsPath, ct).ConfigureAwait(false);
        return library.TopCellCandidates;
    }

    private static async Task<GdsLibrary> ReadLibraryAsync(string gdsPath, CancellationToken ct)
    {
        if (!File.Exists(gdsPath))
            throw new FileNotFoundException($"GDS file not found: {gdsPath}", gdsPath);

        await using var stream = new FileStream(
            gdsPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);
        return await new GdsReader().ReadAsync(stream, ct).ConfigureAwait(false);
    }
}
