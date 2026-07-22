namespace CAP_Core.Solvers.ModeProbe;

/// <summary>
/// A resolved waveguide cross-section for the mode probe, with provenance:
/// <see cref="IsGeometryAssumed"/> is true when any value did not come from an
/// authoritative source (clicked connection / active PDK process) and the UI
/// must show a "geometry assumed — check values" hint.
/// </summary>
/// <param name="WidthMicrometers">Core width in µm.</param>
/// <param name="HeightMicrometers">Core height (thickness) in µm.</param>
/// <param name="SlabHeightMicrometers">Slab height in µm (0 for a strip guide).</param>
/// <param name="CoreIndex">Core material refractive index.</param>
/// <param name="CladIndex">Cladding refractive index.</param>
/// <param name="IsGeometryAssumed">True when any value is a fallback, not PDK/connection data.</param>
/// <param name="SourceDescription">Human-readable provenance summary for the UI.</param>
public sealed record ProbeCrossSection(
    double WidthMicrometers,
    double HeightMicrometers,
    double SlabHeightMicrometers,
    double CoreIndex,
    double CladIndex,
    bool IsGeometryAssumed,
    string SourceDescription)
{
    /// <summary>SOI 220 nm strip-guide defaults used when nothing better is known.</summary>
    public static ProbeCrossSection Default { get; } = new(
        WidthMicrometers: 0.45,
        HeightMicrometers: 0.22,
        SlabHeightMicrometers: 0.0,
        CoreIndex: 3.48,
        CladIndex: 1.44,
        IsGeometryAssumed: true,
        SourceDescription: "built-in SOI defaults");
}
