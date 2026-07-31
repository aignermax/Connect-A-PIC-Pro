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
/// A reconstructed abutment connection between two pins whose absolute
/// positions coincide (within
/// <see cref="GdsHierarchyImportOptions.AbutmentToleranceUm"/>) and whose
/// angles oppose — or between an instance pin and a coincident top-cell port.
/// </summary>
public sealed record GdsPinPair
{
    /// <summary>First endpoint (the instance pin found first in deterministic scan order).</summary>
    public GdsPinEndpoint A { get; init; } = new();

    /// <summary>Second endpoint (an instance pin or a top-cell port).</summary>
    public GdsPinEndpoint B { get; init; } = new();

    /// <summary>
    /// App-space X of the connection point (midpoint of the two coincident pin
    /// positions), in micrometers, relative to the top-cell bbox origin.
    /// </summary>
    public double XUm { get; init; }

    /// <summary>App-space Y of the connection point, in micrometers.</summary>
    public double YUm { get; init; }
}
