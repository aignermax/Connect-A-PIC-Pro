namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// One end of a reconstructed abutment connection: either an instance pin or a
/// top-cell port of the imported circuit.
/// </summary>
public sealed record GdsPinEndpoint
{
    /// <summary>
    /// Index into <see cref="GdsCircuitImport.Instances"/>, or −1 when this
    /// endpoint is a top-cell port.
    /// </summary>
    public int InstanceIndex { get; init; } = -1;

    /// <summary>Pin name (detected label/heuristic name, or the PDK pin name for known components).</summary>
    public string PinName { get; init; } = string.Empty;

    /// <summary>True when this endpoint is a port of the top cell itself (an external circuit port).</summary>
    public bool IsTopLevelPort => InstanceIndex < 0;
}

/// <summary>
/// A reconstructed connection between two pins: either a coincident-pin
/// abutment (positions within
/// <see cref="GdsHierarchyImportOptions.AbutmentToleranceUm"/>, angles
/// opposing), an instance pin coincident with a top-cell port — or a
/// route-derived pair, where a top-cell waveguide polygon drawn between the two
/// pins touches both (<see cref="IsRouteDerived"/>).
/// </summary>
public sealed record GdsPinPair
{
    /// <summary>First endpoint (the instance pin found first in deterministic scan order).</summary>
    public GdsPinEndpoint A { get; init; } = new();

    /// <summary>Second endpoint (an instance pin or a top-cell port).</summary>
    public GdsPinEndpoint B { get; init; } = new();

    /// <summary>
    /// App-space X of the connection point (midpoint of the two pin positions),
    /// in micrometers, relative to the top-cell bbox origin. Informational.
    /// </summary>
    public double XUm { get; init; }

    /// <summary>App-space Y of the connection point, in micrometers.</summary>
    public double YUm { get; init; }

    /// <summary>
    /// True when the pair was derived from a top-cell route polygon touching
    /// both pins (the drawn route IS the connectivity) instead of from
    /// coincident pin positions. Route-derived pairs run before abutment
    /// matching and consume their pins; the placement layer attaches the drawn
    /// geometry as a frozen cached route (<see cref="SourcePolygons"/>) instead
    /// of re-routing, and reports them separately.
    /// </summary>
    public bool IsRouteDerived { get; init; }

    /// <summary>
    /// The top-cell route polygons of the network this pair was derived from
    /// (app-space of the top-cell bbox, like the pair positions) — the drawn
    /// geometry the placement layer turns into the connection's cached route.
    /// Empty for coincident-pin abutment pairs.
    /// </summary>
    public IReadOnlyList<GdsOutlinePolygon> SourcePolygons { get; init; } = Array.Empty<GdsOutlinePolygon>();

    /// <summary>
    /// True when the pair was derived from a top-cell METAL-layer polygon
    /// network — an electrical connection (metal trace), not an optical
    /// waveguide (<see cref="GdsHierarchyImportOptions.MetalRouteLayers"/>).
    /// Metal-derived pairs are always route-derived as well; the placement
    /// layer creates them identically (the connection's kind follows from the
    /// connected pins' signal domains).
    /// </summary>
    public bool IsElectrical { get; init; }
}
