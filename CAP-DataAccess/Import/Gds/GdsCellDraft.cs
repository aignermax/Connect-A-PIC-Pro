namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// A 2D point of a draft outline polygon in micrometers, app-space convention:
/// Y axis points DOWN, origin at the top-left corner of the cell bounding box.
/// </summary>
public readonly record struct GdsOutlinePoint(double X, double Y);

/// <summary>
/// One simplified outline polygon of a <see cref="GdsCellDraft"/>: a closed
/// ring (first point repeated at the end, matching the GDS convention) in
/// app-space coordinates (micrometers, Y-down, origin at the cell bbox
/// top-left). Layer/datatype are kept so the renderer can style per layer.
/// </summary>
public sealed record GdsOutlinePolygon
{
    /// <summary>GDS layer number the polygon came from.</summary>
    public int Layer { get; init; }

    /// <summary>GDS datatype the polygon came from.</summary>
    public int DataType { get; init; }

    /// <summary>Closed ring of vertices (first point repeated at the end).</summary>
    public IReadOnlyList<GdsOutlinePoint> Points { get; init; } = Array.Empty<GdsOutlinePoint>();
}

/// <summary>
/// Pure-data description of one imported GDS cell that has no matching PDK
/// component: everything the service layer needs to build a
/// <c>PdkComponentDraft</c> from it. All coordinates follow the application
/// convention (micrometers, Y-down, origin at the cell bbox top-left), so the
/// pins satisfy the PDK loader rule that they lie inside [0, Width] × [0, Height].
/// </summary>
public sealed record GdsCellDraft
{
    /// <summary>Name of the GDS cell this draft was built from.</summary>
    public string CellName { get; init; } = string.Empty;

    /// <summary>Cell bounding-box width in micrometers.</summary>
    public double WidthUm { get; init; }

    /// <summary>Cell bounding-box height in micrometers.</summary>
    public double HeightUm { get; init; }

    /// <summary>
    /// Pins detected by <see cref="GdsPinDetector"/> on the cell's own port
    /// labels plus the waveguide-edge heuristic over the fully flattened cell
    /// geometry, in app-space coordinates.
    /// </summary>
    public IReadOnlyList<DetectedPin> Pins { get; init; } = Array.Empty<DetectedPin>();

    /// <summary>
    /// Simplified outline polygons of the fully flattened cell (sub-hierarchies
    /// absorbed), capped at
    /// <see cref="GdsHierarchyImportOptions.MaxOutlinePointsPerCell"/> points.
    /// </summary>
    public IReadOnlyList<GdsOutlinePolygon> Outlines { get; init; } = Array.Empty<GdsOutlinePolygon>();

    /// <summary>
    /// Nazca raw-code snippet that rebuilds this cell's geometry for preview and
    /// GDS export round-trip: a <c>component()</c> function returning
    /// <c>nd.load_gds(...)</c> for this cell, matching the raw-code execution
    /// contract (the file is imported as a Python module and its
    /// <c>component()</c> callable must return a Nazca cell). The
    /// <c>{GdsFileName}</c> token (<see cref="GdsHierarchyImporter.GdsFileNameToken"/>)
    /// is a placeholder: the UI layer replaces it with the bare .gds file name
    /// after copying the source file next to the user-PDK JSON, so the exported
    /// Python resolves it relative to its working directory.
    /// </summary>
    public string RawCode { get; init; } = string.Empty;

    /// <summary>
    /// Backend tag for <see cref="RawCode"/>, mirroring
    /// <c>PdkComponentDraft.RawCodeBackend</c>: always "nazca" (nd.load_gds is
    /// the Nazca API).
    /// </summary>
    public string RawCodeBackend { get; init; } = "nazca";
}
