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
    /// (Layer, Datatype) pairs whose top-cell-OWN polygons are imported as frozen
    /// route paths on the created group (explode mode). Default: (1, 0), the
    /// gdsfactory waveguide-core layer, plus (1111, 0), the layer of nazca's
    /// default interconnect (<c>nd.strt</c>/<c>nd.bend</c>) — what our own
    /// exporter flattens routed connections into, so re-importing a Lunima export
    /// shows its routes without any configuration. Kept separate from
    /// <see cref="GdsPinDetectionOptions.WaveguideLayers"/>: that list feeds the
    /// pin edge heuristic, where a routing layer that never touches a cell bbox
    /// edge would only spawn spurious pins.
    /// </summary>
    public IReadOnlyList<(int Layer, int Datatype)> RouteLayers { get; init; } = [(1, 0), (1111, 0)];

    /// <summary>
    /// (Layer, Datatype) pairs whose top-cell-OWN polygons are treated as METAL
    /// traces (electrical routing): a metal polygon network touched by exactly
    /// two pins becomes an ELECTRICAL connection (not a waveguide connection —
    /// the pins' signal domains decide the created connection's kind, with
    /// unknown-kind pins inferred electrical); unconsumed metal polygons are
    /// imported as frozen paths like their waveguide counterparts. Kept
    /// separate from <see cref="RouteLayers"/>: optical and metal polygon
    /// networks must never merge into one connection.
    ///
    /// Default: null = AUTO. Our exporters' metal layer numbers are NOT
    /// universal truth (a real foundry file carried optical routes on
    /// (12, 0) and every reconstructed connection came back electrical), so
    /// the importer applies <see cref="LunimaMetalRouteLayers"/> only when the
    /// file is recognizably our own export (<see cref="GdsOwnExportSentinel"/>)
    /// and treats foreign files as having NO metal route layers until the user
    /// supplies a mapping (import dialog / options).
    /// </summary>
    public IReadOnlyList<(int Layer, int Datatype)>? MetalRouteLayers { get; init; }

    /// <summary>
    /// The metal route layers OUR OWN exporters flatten electrical routing
    /// into: (11, 0) metal traces and (12, 0) bridge markers
    /// (<c>MetalRoutingSpec</c> defaults). Applied for
    /// <see cref="MetalRouteLayers"/> = AUTO on recognizably-own exports only.
    /// </summary>
    public static readonly IReadOnlyList<(int Layer, int Datatype)> LunimaMetalRouteLayers =
        [(11, 0), (12, 0)];

    /// <summary>
    /// Replaces AUTO (null) layer lists with their concrete values:
    /// <see cref="LunimaMetalRouteLayers"/> /
    /// <see cref="GdsPinDetectionOptions.LunimaElectricalLayers"/> when
    /// <paramref name="isOwnExport"/> (the file carries the Lunima export
    /// sentinel), empty lists otherwise. Explicitly configured lists are
    /// returned verbatim — a user-supplied layer mapping always wins.
    /// </summary>
    public GdsHierarchyImportOptions ResolveLayerDefaults(bool isOwnExport) => this with
    {
        MetalRouteLayers = MetalRouteLayers
            ?? (isOwnExport ? LunimaMetalRouteLayers : []),
        PinDetection = PinDetection with
        {
            ElectricalLayers = PinDetection.ElectricalLayers
                ?? (isOwnExport ? GdsPinDetectionOptions.LunimaElectricalLayers : []),
        },
    };

    /// <summary>
    /// Maximum distance in micrometers between two absolute pin positions for
    /// them to count as abutting (forming a connection). Default: 0.05 µm.
    /// </summary>
    public double AbutmentToleranceUm { get; init; } = 0.05;

    /// <summary>
    /// Maximum distance in micrometers between a pin position and a top-cell
    /// route polygon for the pin to count as touching it (route-derivation, see
    /// <c>GdsRouteConnectivityMatcher</c>). Deliberately much wider than
    /// <see cref="AbutmentToleranceUm"/>: a drawn route ends ON its pins, so a
    /// generous window catches pins that sit a fraction of a micron off the
    /// polygon end (PDK cell swaps, rounding) without ever bridging the gap to
    /// an unrelated neighbor. Default: 1.0 µm.
    /// </summary>
    public double PinTouchToleranceUm { get; init; } = 1.0;

    /// <summary>
    /// Maximum distance in micrometers between two top-cell route polygons for
    /// them to count as one connected network (route-derivation chaining, see
    /// <c>GdsRouteConnectivityMatcher</c>). Deliberately TIGHT: consecutive
    /// segments of one exported route share their joint exactly (within export
    /// rounding), while independently routed traces can run at a pitch far
    /// below <see cref="PinTouchToleranceUm"/> — a 10 µm-wide metal trace pair
    /// 0.6 µm apart must stay two networks, or parallel buses would merge into
    /// one junction blob. True crossings still merge (their polygons genuinely
    /// overlap) and become junction-frozen, as intended. Default: 0.05 µm.
    /// </summary>
    public double PolygonChainToleranceUm { get; init; } = 0.05;

    /// <summary>
    /// Ramer-Douglas-Peucker tolerance in micrometers for simplifying draft
    /// outline polygons. Default: 0.05 µm.
    /// </summary>
    public double OutlineSimplificationToleranceUm { get; init; } = 0.05;

    /// <summary>
    /// Maximum total outline points kept per cell draft. When simplification at
    /// the configured tolerance exceeds this, the tolerance is raised
    /// adaptively; as a last resort the smallest-area polygons are dropped
    /// (with a warning). Default: 8000 — outline geometry is built once per
    /// template and drawn from a cached Skia geometry, so detail is cheap; the
    /// cap only guards degenerate multi-million-point cells.
    /// </summary>
    public int MaxOutlinePointsPerCell { get; init; } = 8000;

    /// <summary>
    /// Resolves a GDS cell name to an existing PDK component. Called with the
    /// exact cell name first; on a miss the importer retries with gdsfactory
    /// hash suffixes (<c>_&lt;hex&gt;</c>) stripped. Null (default) treats every
    /// cell as unknown (all become drafts).
    /// </summary>
    public Func<string, KnownComponent?>? ResolveKnownComponent { get; init; }

    /// <summary>
    /// Throws when a tunable is out of range: tolerances and the outline-point
    /// cap must be non-negative (a negative cap would make the outline
    /// simplifier drop every polygon). Called by <see cref="GdsHierarchyImporter"/>
    /// before any work.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A tolerance or the cap is negative.</exception>
    public void Validate()
    {
        if (AbutmentToleranceUm < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(AbutmentToleranceUm), AbutmentToleranceUm, "The abutment tolerance must be ≥ 0.");
        }
        if (PinTouchToleranceUm < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PinTouchToleranceUm), PinTouchToleranceUm, "The pin-touch tolerance must be ≥ 0.");
        }
        if (PolygonChainToleranceUm < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PolygonChainToleranceUm), PolygonChainToleranceUm, "The polygon-chain tolerance must be ≥ 0.");
        }
        if (OutlineSimplificationToleranceUm < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(OutlineSimplificationToleranceUm), OutlineSimplificationToleranceUm,
                "The outline simplification tolerance must be ≥ 0.");
        }
        if (MaxOutlinePointsPerCell < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxOutlinePointsPerCell), MaxOutlinePointsPerCell,
                "The outline-point cap must be ≥ 0.");
        }
        PinDetection.Validate();
    }
}
