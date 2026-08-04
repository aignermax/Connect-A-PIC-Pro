namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// Result of <see cref="GdsHierarchyImporter.ImportAsync"/>: a pure-data
/// description of the imported circuit — component drafts for unknown cells,
/// placed instances, reconstructed abutment connections and user-presentable
/// warnings. No canvas, <c>Component</c> or UI objects are created; the
/// service layer turns this into concrete placements.
/// </summary>
public sealed record GdsCircuitImport
{
    /// <summary>The mode the import ran in.</summary>
    public GdsHierarchyImportMode Mode { get; init; }

    /// <summary>Top cell of the imported layout.</summary>
    public string TopCellName { get; init; } = string.Empty;

    /// <summary>
    /// Bounding box of the whole top-cell hierarchy in micrometers, in
    /// GDS-native Y-up coordinates (same convention as
    /// <see cref="GdsImportResult.BoundingBox"/>). App-space coordinates in this
    /// result are relative to this box's top-left corner: appX = gdsX − MinX,
    /// appY = MaxY − gdsY.
    /// </summary>
    public GdsBoundingBox BoundingBox { get; init; }

    /// <summary>
    /// Component drafts for cells without a matching PDK component (in
    /// explode mode: every distinct unknown cell referenced by the top cell; in
    /// black-box mode: the single top-cell draft).
    /// </summary>
    public IReadOnlyList<GdsCellDraft> ImportedCellDrafts { get; init; } = Array.Empty<GdsCellDraft>();

    /// <summary>Placed instances in GDS placement order (empty in black-box mode).</summary>
    public IReadOnlyList<GdsPlacedInstance> Instances { get; init; } = Array.Empty<GdsPlacedInstance>();

    /// <summary>Reconstructed abutment connections (empty in black-box mode).</summary>
    public IReadOnlyList<GdsPinPair> Connections { get; init; } = Array.Empty<GdsPinPair>();

    /// <summary>
    /// The top cell's OWN polygons on the configured route layers
    /// (<see cref="GdsHierarchyImportOptions.RouteLayers"/>; routing geometry), in
    /// app-space of <see cref="BoundingBox"/> (Y-down, origin at the
    /// bbox top-left — the same frame <see cref="GdsPlacedInstance.PositionXUm"/>
    /// uses). Explode mode only; the service layer attaches them to the created
    /// group as frozen, pin-less, non-re-routable paths. Geometry contributed by
    /// child instances is excluded — it renders through the placed components'
    /// own outlines.
    /// </summary>
    public IReadOnlyList<GdsOutlinePolygon> TopCellWaveguidePolygons { get; init; } =
        Array.Empty<GdsOutlinePolygon>();

    /// <summary>User-presentable warnings collected during the import, in encounter order.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Informational notes collected during the import (known-component
    /// resolutions, skipped zero-geometry/export-artifact cells), in encounter
    /// order. These describe normal behavior — unlike <see cref="Warnings"/>,
    /// no user action is needed; the UI shows them at info level.
    /// </summary>
    public IReadOnlyList<string> Infos { get; init; } = Array.Empty<string>();
}
