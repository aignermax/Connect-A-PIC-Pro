namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// How <see cref="GdsHierarchyImporter"/> treats the cell hierarchy of the
/// imported layout.
/// </summary>
public enum GdsHierarchyImportMode
{
    /// <summary>
    /// Direct children of the top cell become placed instances: known cells
    /// reference existing PDK components, unknown cells become new component
    /// drafts (their own subtrees absorbed), and abutting pins are reconstructed
    /// into connections.
    /// </summary>
    ExplodeHierarchy,

    /// <summary>
    /// The whole top cell becomes a single component draft (pins + outlines);
    /// no instance or connection reconstruction happens.
    /// </summary>
    BlackBox,
}

/// <summary>
/// An existing PDK component that a GDS cell maps to, resolved by the caller
/// (UI/service layer) via <see cref="GdsHierarchyImportOptions.ResolveKnownComponent"/>.
/// Carries the component's physical pins so the importer can reconstruct
/// abutment connections with authoritative PDK pin names and positions.
/// </summary>
/// <param name="Identifier">Component identifier within its PDK (e.g. "mmi1x2").</param>
/// <param name="PdkSource">Name of the PDK the component comes from.</param>
/// <param name="WidthUm">Component width in micrometers (should equal the GDS cell bbox width).</param>
/// <param name="HeightUm">Component height in micrometers (should equal the GDS cell bbox height).</param>
/// <param name="Pins">
/// Physical pins in the application's per-component convention: micrometers,
/// Y-down, origin at the top-left of the component's <paramref name="WidthUm"/> ×
/// <paramref name="HeightUm"/> box, angles in the app convention (0° = east,
/// 90° = down). Same shape <see cref="GdsPinDetector"/> emits.
/// </param>
public sealed record KnownComponent(
    string Identifier,
    string PdkSource,
    double WidthUm,
    double HeightUm,
    IReadOnlyList<DetectedPin> Pins);

/// <summary>
/// Tunables for <see cref="GdsHierarchyImporter"/>.
/// </summary>
public sealed record GdsHierarchyImportOptions
{
    /// <summary>Hierarchy handling mode. Default: <see cref="GdsHierarchyImportMode.ExplodeHierarchy"/>.</summary>
    public GdsHierarchyImportMode Mode { get; init; } = GdsHierarchyImportMode.ExplodeHierarchy;

    /// <summary>Pin detection configuration forwarded to <see cref="GdsPinDetector"/>.</summary>
    public GdsPinDetectionOptions PinDetection { get; init; } = new();

    /// <summary>
    /// Maximum distance in micrometers between two absolute pin positions for
    /// them to count as abutting (forming a connection). Default: 0.05 µm.
    /// </summary>
    public double AbutmentToleranceUm { get; init; } = 0.05;

    /// <summary>
    /// Ramer-Douglas-Peucker tolerance in micrometers for simplifying draft
    /// outline polygons. Default: 0.05 µm.
    /// </summary>
    public double OutlineSimplificationToleranceUm { get; init; } = 0.05;

    /// <summary>
    /// Maximum total outline points kept per cell draft. When simplification at
    /// the configured tolerance exceeds this, the tolerance is raised
    /// adaptively; as a last resort the smallest-area polygons are dropped
    /// (with a warning). Default: 2000.
    /// </summary>
    public int MaxOutlinePointsPerCell { get; init; } = 2000;

    /// <summary>
    /// Resolves a GDS cell name to an existing PDK component. Called with the
    /// exact cell name first; on a miss the importer retries with gdsfactory
    /// hash suffixes (<c>_&lt;hex&gt;</c>) stripped. Null (default) treats every
    /// cell as unknown (all become drafts).
    /// </summary>
    public Func<string, KnownComponent?>? ResolveKnownComponent { get; init; }
}
