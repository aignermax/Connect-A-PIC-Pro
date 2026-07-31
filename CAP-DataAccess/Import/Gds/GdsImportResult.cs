namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// Result of importing a GDS file for a chosen top cell: the library-level
/// structure plus the resolved geometry of the top cell's hierarchy.
/// All coordinates are micrometers with the GDS-native Y-up orientation.
/// </summary>
public class GdsImportResult
{
    /// <summary>Library name from the LIBNAME record.</summary>
    public string LibraryName { get; set; } = string.Empty;

    /// <summary>Names of all cells in the library, in file order.</summary>
    public IReadOnlyList<string> CellNames { get; set; } = Array.Empty<string>();

    /// <summary>Cells not referenced by any other cell (possible top cells).</summary>
    public IReadOnlyList<string> TopCellCandidates { get; set; } = Array.Empty<string>();

    /// <summary>The top cell this result was resolved for.</summary>
    public string TopCellName { get; set; } = string.Empty;

    /// <summary>Bounding box of the whole top-cell hierarchy in micrometers.</summary>
    public GdsBoundingBox BoundingBox { get; set; }

    /// <summary>All polygons of the hierarchy in top-cell coordinates (micrometers, Y-up).</summary>
    public IReadOnlyList<GdsPolygon> Polygons { get; set; } = Array.Empty<GdsPolygon>();

    /// <summary>All texts of the hierarchy in top-cell coordinates (micrometers, Y-up).</summary>
    public IReadOnlyList<GdsText> Texts { get; set; } = Array.Empty<GdsText>();

    /// <summary>
    /// Direct child instances of the top cell (array references expanded to one
    /// entry per member), with transforms resolved into top-cell coordinates.
    /// </summary>
    public IReadOnlyList<GdsInstance> Instances { get; set; } = Array.Empty<GdsInstance>();
}
