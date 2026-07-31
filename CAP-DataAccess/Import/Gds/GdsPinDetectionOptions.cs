namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// Tunables for <see cref="GdsPinDetector"/>. The defaults follow the gdsfactory
/// conventions: port labels are TEXT elements on layer (1, 10) and waveguide
/// cores are polygons on layer (1, 0).
/// </summary>
public sealed record GdsPinDetectionOptions
{
    /// <summary>
    /// (Layer, Datatype) pairs whose TEXT elements are treated as pin labels.
    /// Default: (1, 10), the gdsfactory port-label layer. Other tools place pin
    /// markers elsewhere (e.g. SiEPIC-Tools uses dedicated PinRec layers) — callers
    /// targeting those PDKs must configure this list; we deliberately do not
    /// hardcode a second default.
    /// </summary>
    public IReadOnlyList<(int Layer, int Datatype)> PortLayers { get; init; } = [(1, 10)];

    /// <summary>
    /// (Layer, Datatype) pairs whose polygons count as waveguides for the
    /// bounding-box edge heuristic. Default: (1, 0).
    /// </summary>
    public IReadOnlyList<(int Layer, int Datatype)> WaveguideLayers { get; init; } = [(1, 0)];

    /// <summary>
    /// Distance in micrometers within which a segment endpoint or text anchor is
    /// considered to lie on a bounding-box edge line. Default: 0.001 µm = 1 nm
    /// (one database unit in a typical 1 nm grid).
    /// </summary>
    public double EdgeTouchToleranceUm { get; init; } = 0.001;

    /// <summary>Heuristic pins narrower than this (µm) are discarded as spurious touches. Default: 0.1.</summary>
    public double MinPinWidthUm { get; init; } = 0.1;

    /// <summary>Heuristic pins wider than this (µm) are discarded as slab/boundary contacts. Default: 100.</summary>
    public double MaxPinWidthUm { get; init; } = 100.0;
}
