using CAP_DataAccess.Import.Gds;

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
    /// 'nazca' cell) replaced by the cell they wrap.
    /// </summary>
    public IReadOnlyList<string> TopCellCandidates { get; init; } = Array.Empty<string>();

    /// <summary>Per-candidate instance counts, aligned with <see cref="TopCellCandidates"/>.</summary>
    public IReadOnlyList<GdsTopCellSummary> TopCells { get; init; } = Array.Empty<GdsTopCellSummary>();
}
