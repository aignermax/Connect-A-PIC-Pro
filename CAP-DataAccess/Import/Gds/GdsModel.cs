namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// A 2D point in micrometers. GDS native orientation is preserved: Y axis points up.
/// Callers that render into a Y-down coordinate system must flip Y themselves.
/// </summary>
public readonly record struct GdsPoint(double X, double Y);

/// <summary>
/// Parsed contents of a GDSII stream file: a flat dictionary of cells plus the
/// unit conversion factors from the UNITS record. All coordinates stored in the
/// cells have already been converted from database units to micrometers.
/// </summary>
public class GdsLibrary
{
    /// <summary>Library name from the LIBNAME record (empty if the record is absent).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>All cells in the library, keyed by (case-sensitive) cell name.</summary>
    public Dictionary<string, GdsCell> Cells { get; } = new();

    /// <summary>
    /// User units per database unit (first UNITS real, typically 1e-3 when the
    /// user unit is 1 µm and the database unit is 1 nm). Informational only —
    /// coordinate conversion uses <see cref="DatabaseUnitInMeters"/>.
    /// </summary>
    public double UserUnitsPerDatabaseUnit { get; set; }

    /// <summary>
    /// Size of one database unit in meters (second UNITS real, typically 1e-9).
    /// </summary>
    public double DatabaseUnitInMeters { get; set; }

    /// <summary>Multiplication factor converting a database-unit length to micrometers.</summary>
    public double DatabaseUnitsToMicrometers => DatabaseUnitInMeters / 1e-6;

    /// <summary>
    /// Names of cells that are not referenced by any other cell — the candidates
    /// for the top cell of the layout. Computed on demand so it stays correct
    /// regardless of the order cells appeared in the stream.
    /// </summary>
    public IReadOnlyList<string> TopCellCandidates
    {
        get
        {
            var referenced = new HashSet<string>();
            foreach (var cell in Cells.Values)
            {
                foreach (var element in cell.Elements)
                {
                    if (element is GdsReference reference)
                        referenced.Add(reference.CellName);
                }
            }

            return Cells.Keys.Where(name => !referenced.Contains(name)).ToList();
        }
    }
}

/// <summary>
/// A GDS cell (structure): a named, ordered list of elements.
/// </summary>
public class GdsCell
{
    /// <summary>Cell name from the STRNAME record.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Elements in the order they appeared in the stream.</summary>
    public List<GdsElement> Elements { get; } = new();
}

/// <summary>Base type for the elements a <see cref="GdsCell"/> can contain.</summary>
public abstract record GdsElement
{
    // Closed hierarchy: only the derived types in this file exist.
    private protected GdsElement() { }
}

/// <summary>
/// A closed polygon (GDS BOUNDARY, or BOX mapped to a polygon). Points are in
/// micrometers, Y-up; GDS polygons repeat the first point as the last point.
/// </summary>
public sealed record GdsPolygon : GdsElement
{
    /// <summary>GDS layer number.</summary>
    public int Layer { get; init; }

    /// <summary>GDS datatype.</summary>
    public int DataType { get; init; }

    /// <summary>Vertices in micrometers (first point repeated at the end for closed polygons).</summary>
    public IReadOnlyList<GdsPoint> Points { get; init; } = Array.Empty<GdsPoint>();
}

/// <summary>
/// A GDS PATH: a polyline with a width and end-cap style. Stored as centerline
/// points plus width; converting to a polygon outline is left to consumers.
/// </summary>
public sealed record GdsPath : GdsElement
{
    /// <summary>GDS layer number.</summary>
    public int Layer { get; init; }

    /// <summary>GDS datatype.</summary>
    public int DataType { get; init; }

    /// <summary>
    /// Path width in micrometers. A negative WIDTH record means "absolute width"
    /// (unaffected by magnification); the flag is not honored here and the
    /// absolute value is stored.
    /// </summary>
    public double WidthMicrometers { get; init; }

    /// <summary>PATHTYPE: 0 = flush ends, 1 = round ends, 2 = extended by half width.</summary>
    public int PathType { get; init; }

    /// <summary>Centerline vertices in micrometers, Y-up.</summary>
    public IReadOnlyList<GdsPoint> Points { get; init; } = Array.Empty<GdsPoint>();
}

/// <summary>
/// A GDS TEXT label. Only the string, position, layer/texttype and rotation are
/// captured; font/presentation attributes are ignored.
/// </summary>
public sealed record GdsText : GdsElement
{
    /// <summary>GDS layer number.</summary>
    public int Layer { get; init; }

    /// <summary>GDS texttype.</summary>
    public int TextType { get; init; }

    /// <summary>Label content (NUL padding trimmed).</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Anchor position in micrometers, Y-up.</summary>
    public GdsPoint Position { get; init; }

    /// <summary>Rotation in degrees, counter-clockwise in the Y-up plane.</summary>
    public double AngleDegrees { get; init; }
}

/// <summary>
/// A reference to another cell (SREF, or AREF for arrays). The referenced cell
/// may be defined later in the stream — references are resolved by the flattener,
/// not the reader.
/// </summary>
public sealed record GdsReference : GdsElement
{
    /// <summary>Name of the referenced cell (SNAME).</summary>
    public string CellName { get; init; } = string.Empty;

    /// <summary>Placement offset in micrometers, Y-up.</summary>
    public GdsPoint Offset { get; init; }

    /// <summary>Rotation in degrees, counter-clockwise, applied after reflection.</summary>
    public double AngleDegrees { get; init; }

    /// <summary>Magnification factor applied to the referenced geometry (1 = none).</summary>
    public double Magnification { get; init; } = 1.0;

    /// <summary>STRANS reflection flag: mirror about the X axis (Y → -Y) before rotation.</summary>
    public bool Reflected { get; init; }

    /// <summary>Raw STRANS bit array (bits 1/2 = absolute angle/magnification; informational).</summary>
    public int TransformFlags { get; init; }

    /// <summary>Array column count (AREF; 1 for a plain SREF).</summary>
    public int Columns { get; init; } = 1;

    /// <summary>Array row count (AREF; 1 for a plain SREF).</summary>
    public int Rows { get; init; } = 1;

    /// <summary>Spacing between array columns in micrometers (AREF).</summary>
    public double ColumnSpacingMicrometers { get; init; }

    /// <summary>Spacing between array rows in micrometers (AREF).</summary>
    public double RowSpacingMicrometers { get; init; }

    /// <summary>True when this reference came from an AREF record and expands to multiple instances.</summary>
    public bool IsArray => Columns > 1 || Rows > 1;
}

/// <summary>
/// An axis-aligned bounding box in micrometers, Y-up.
/// </summary>
public readonly record struct GdsBoundingBox(double MinX, double MinY, double MaxX, double MaxY)
{
    /// <summary>The all-zero box returned for cells without any geometry.</summary>
    public static GdsBoundingBox Empty => new(0, 0, 0, 0);

    /// <summary>Width (MaxX − MinX) in micrometers.</summary>
    public double Width => MaxX - MinX;

    /// <summary>Height (MaxY − MinY) in micrometers.</summary>
    public double Height => MaxY - MinY;

    /// <summary>Returns the smallest box containing both this box and <paramref name="other"/>.</summary>
    public GdsBoundingBox Union(GdsBoundingBox other) =>
        new(Math.Min(MinX, other.MinX), Math.Min(MinY, other.MinY),
            Math.Max(MaxX, other.MaxX), Math.Max(MaxY, other.MaxY));

    /// <summary>Returns the smallest box containing this box and the point.</summary>
    public GdsBoundingBox Include(GdsPoint point) =>
        new(Math.Min(MinX, point.X), Math.Min(MinY, point.Y),
            Math.Max(MaxX, point.X), Math.Max(MaxY, point.Y));
}

/// <summary>
/// One placed instance of a referenced cell, as an entry of
/// <see cref="GdsCellFlattener.GetInstanceTree"/>: the referenced cell name plus
/// its transform resolved into the top cell's coordinate space. Array references
/// yield one instance per expanded member.
/// </summary>
public sealed record GdsInstance
{
    /// <summary>Name of the referenced (child) cell.</summary>
    public string CellName { get; init; } = string.Empty;

    /// <summary>Instance origin in micrometers, Y-up, in the top cell's space.</summary>
    public GdsPoint Offset { get; init; }

    /// <summary>Rotation in degrees, counter-clockwise.</summary>
    public double AngleDegrees { get; init; }

    /// <summary>Magnification applied to the child geometry.</summary>
    public double Magnification { get; init; } = 1.0;

    /// <summary>True when the child is mirrored about its X axis.</summary>
    public bool Reflected { get; init; }
}

/// <summary>
/// Where a flattened text came from: the cell that OWNS the label (the leaf
/// cell of the flatten walk, not the requested top cell) and which occurrence
/// of that cell it rode in with (0-based, in flatten walk order — the same
/// order <see cref="GdsCellFlattener.GetInstanceTree"/> expands instances, so
/// occurrence numbers match the importer's <c>{cell}#{n}</c> instance naming
/// for direct children).
/// </summary>
public readonly record struct GdsTextOrigin(string CellName, int Occurrence);

/// <summary>
/// Result of <see cref="GdsCellFlattener.Flatten"/>: all polygons and texts of a
/// cell hierarchy transformed into the top cell's coordinate space (micrometers,
/// Y-up preserved).
/// </summary>
public class FlattenedGdsCell
{
    /// <summary>Name of the cell that was flattened.</summary>
    public string CellName { get; set; } = string.Empty;

    /// <summary>All polygons, including those pulled in through references, in top-cell coordinates.</summary>
    public List<GdsPolygon> Polygons { get; } = new();

    /// <summary>All texts, including those pulled in through references, in top-cell coordinates.</summary>
    public List<GdsText> Texts { get; } = new();

    /// <summary>
    /// Per-entry provenance of <see cref="Texts"/> (index-aligned), filled only
    /// by <see cref="GdsCellFlattener.Flatten"/> — manually assembled detection
    /// cells leave this empty. Consumers that attribute labels to their source
    /// cells (black-box pin detection) must check the count before indexing.
    /// </summary>
    public List<GdsTextOrigin> TextOrigins { get; } = new();
}
