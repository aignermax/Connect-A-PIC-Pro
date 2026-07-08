namespace CAP_Core.Routing.MetalRouting;

/// <summary>
/// Process-derived parameters for routing electrical connections as metal traces
/// (issue #682): trace width, the GDS layer the metal is drawn on, and the
/// waveguide-crossing policy. Built from the active process cross-sections by the
/// data-access layer; <see cref="Default"/> is the fallback when the process
/// declares no metal cross-section.
/// </summary>
/// <param name="TraceWidthMicrometers">Metal trace width in µm.</param>
/// <param name="MetalGdsLayer">GDS layer number the metal traces are drawn on.</param>
/// <param name="MetalGdsDatatype">GDS datatype for the metal layer.</param>
/// <param name="CrossingPolicy">How metal-over-waveguide crossings are handled.</param>
/// <param name="BridgeGdsLayer">GDS layer number for bridge markers at crossings.</param>
public sealed record MetalRoutingSpec(
    double TraceWidthMicrometers,
    int MetalGdsLayer,
    int MetalGdsDatatype,
    ElectricalCrossingPolicy CrossingPolicy,
    int BridgeGdsLayer)
{
    /// <summary>Fallback metal trace width in µm when the process declares no metal cross-section.</summary>
    public const double DefaultTraceWidthMicrometers = 10.0;

    /// <summary>Fallback GDS layer for metal traces (layer 11, "ElecRec" convention — see #519).</summary>
    public const int DefaultMetalGdsLayer = 11;

    /// <summary>Fallback GDS datatype for the metal layer.</summary>
    public const int DefaultMetalGdsDatatype = 0;

    /// <summary>Fallback GDS layer for bridge markers.</summary>
    public const int DefaultBridgeGdsLayer = 12;

    /// <summary>
    /// Conservative fallback spec used when no process information is available:
    /// 10 µm traces on layer 11/0, direct crossings allowed.
    /// </summary>
    public static MetalRoutingSpec Default { get; } = new(
        DefaultTraceWidthMicrometers,
        DefaultMetalGdsLayer,
        DefaultMetalGdsDatatype,
        ElectricalCrossingPolicy.DirectCrossingAllowed,
        DefaultBridgeGdsLayer);
}
