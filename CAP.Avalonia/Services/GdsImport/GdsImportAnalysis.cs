using CAP_DataAccess.Import.Gds;
using CAP_DataAccess.Import.Gds.LayerCensus;

namespace CAP.Avalonia.Services.GdsImport;

/// <summary>
/// Per-top-cell summary for <see cref="GdsImportAnalysis"/>: the cell name and
/// how many direct instances (SREF placements plus AREF members) it contains —
/// the count of canvas components an explode-mode import of that cell yields.
/// </summary>
public sealed record GdsTopCellSummary(string CellName, int DirectInstanceCount);

/// <summary>
/// Result of <see cref="GdsImportService.AnalyzeAsync"/>: everything the import
/// dialog needs before the user commits to an import — top-cell candidates to
/// choose from and a size summary of the library.
/// </summary>
public sealed record GdsImportAnalysis
{
    /// <summary>Library name from the GDS LIBNAME record (may be empty).</summary>
    public string LibraryName { get; init; } = string.Empty;

    /// <summary>Total number of cells defined in the library.</summary>
    public int CellCount { get; init; }

    /// <summary>
    /// The candidates for the layout's top cell, in file order: cells not
    /// referenced by any other cell, with pure pass-through wrappers (no own
    /// geometry, exactly one untransformed reference — e.g. nazca's default
    /// 'nazca' cell) replaced by the cell they wrap, and metadata sentinel
    /// cells (name wrapped in <c>$$$</c>, e.g. kfactory's
    /// <c>$$$CONTEXT_INFO$$$</c>) filtered out. Never empty — the analysis
    /// throws instead of offering no (or only junk) candidates.
    /// </summary>
    public IReadOnlyList<string> TopCellCandidates { get; init; } = Array.Empty<string>();

    /// <summary>Per-candidate instance counts, aligned with <see cref="TopCellCandidates"/>.</summary>
    public IReadOnlyList<GdsTopCellSummary> TopCells { get; init; } = Array.Empty<GdsTopCellSummary>();

    /// <summary>
    /// The file's layer census: every (layer, datatype) pair present with
    /// polygon/path/text counts — the facts the dialog shows next to the layer
    /// fields so the user never types layer numbers blind.
    /// </summary>
    public IReadOnlyList<GdsLayerCensusEntry> LayerCensus { get; init; } = Array.Empty<GdsLayerCensusEntry>();

    /// <summary>
    /// The parsed library behind this analysis. The import dialog hands it back
    /// to <see cref="GdsImportService.ImportAsync"/> so a large file is not
    /// parsed a second time; the import then works on the snapshot the user
    /// picked the top cell from, even if the file changed on disk in between.
    /// </summary>
    public GdsLibrary? Library { get; init; }
}
