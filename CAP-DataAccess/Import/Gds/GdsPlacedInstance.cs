namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// One placed cell instance of the imported circuit, in the application's
/// circuit-space convention: micrometers, Y-down, origin at the top-left corner
/// of the top cell's bounding box.
/// </summary>
public sealed record GdsPlacedInstance
{
    /// <summary>
    /// Deterministic instance key: <c>{CellName}#{n}</c> where n counts the
    /// occurrences of that cell in placement order (0-based).
    /// </summary>
    public string InstanceName { get; init; } = string.Empty;

    /// <summary>Name of the GDS cell this instance places.</summary>
    public string CellName { get; init; } = string.Empty;

    /// <summary>
    /// Identifier of the existing PDK component this instance references, or
    /// null when the cell is unknown (<see cref="CellDraftName"/> set instead).
    /// </summary>
    public string? KnownComponentIdentifier { get; init; }

    /// <summary>PDK the known component comes from; null for draft instances.</summary>
    public string? PdkSource { get; init; }

    /// <summary>
    /// Name of the <see cref="GdsCellDraft"/> (in
    /// <see cref="GdsCircuitImport.ImportedCellDrafts"/>) this instance places,
    /// or null when it references a known PDK component. Exactly one of
    /// <see cref="KnownComponentIdentifier"/> / <see cref="CellDraftName"/> is set.
    /// </summary>
    public string? CellDraftName { get; init; }

    /// <summary>
    /// App-space X of the placed instance's axis-aligned bounding box top-left
    /// corner (the true GDS transform applied to the cell bbox), in micrometers.
    /// </summary>
    public double PositionXUm { get; init; }

    /// <summary>App-space Y of the placed bounding box top-left corner, in micrometers.</summary>
    public double PositionYUm { get; init; }

    /// <summary>
    /// Placement rotation in cardinal degrees (0/90/180/270), in the
    /// application's own convention: it adds to the component's pin angles
    /// exactly like <c>Component.RotationDegrees</c> does
    /// (pin world angle = local angle + RotationDegrees). Equals the GDS angle
    /// negated modulo 360 (GDS rotates counter-clockwise in a Y-up plane), so a
    /// GDS angle of 90° becomes 270° here. Non-cardinal GDS angles are snapped
    /// to the nearest cardinal (with a warning).
    /// </summary>
    public double RotationDegrees { get; init; }

    /// <summary>
    /// True when the GDS reference carries the STRANS mirror flag. The core
    /// <c>Component</c> model has no mirroring support, so the instance is
    /// placed unreflected (a warning is emitted); connection reconstruction
    /// still uses the true reflected transform.
    /// </summary>
    public bool Reflected { get; init; }
}
