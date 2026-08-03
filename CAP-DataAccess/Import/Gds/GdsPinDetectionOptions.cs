namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// Tunables for <see cref="GdsPinDetector"/>. The defaults follow the gdsfactory
/// conventions — port labels are TEXT elements on layer (1, 10) and waveguide
/// cores are polygons on layer (1, 0) — plus nazca demofab's black-box pin-text
/// layer (501, 1): the application's own Nazca export places demofab cells whose
/// pin labels live there, so re-importing our own GDS needs it recognized.
/// </summary>
public sealed record GdsPinDetectionOptions
{
    /// <summary>
    /// (Layer, Datatype) pairs whose TEXT elements are treated as pin labels.
    /// Defaults: (1, 10), the gdsfactory port-label layer, and (501, 1), nazca
    /// demofab's <c>bb_pin_text</c> layer (demofab's layer table). Other tools
    /// place pin markers elsewhere (e.g. SiEPIC-Tools uses dedicated PinRec
    /// layers) — callers targeting those PDKs must configure this list; we
    /// deliberately do not hardcode further defaults.
    /// </summary>
    public IReadOnlyList<(int Layer, int Datatype)> PortLayers { get; init; } = [(1, 10), (501, 1)];

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

    /// <summary>
    /// Throws when the width window is inconsistent (<see cref="MinPinWidthUm"/>
    /// above <see cref="MaxPinWidthUm"/>) or <see cref="EdgeTouchToleranceUm"/>
    /// is negative. Called by <see cref="GdsPinDetector"/> before detection.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The edge-touch tolerance is negative.</exception>
    /// <exception cref="ArgumentException">The pin-width window is inverted.</exception>
    public void Validate()
    {
        if (EdgeTouchToleranceUm < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(EdgeTouchToleranceUm), EdgeTouchToleranceUm, "The edge-touch tolerance must be ≥ 0.");
        }
        if (MinPinWidthUm > MaxPinWidthUm)
        {
            throw new ArgumentException(
                $"MinPinWidthUm must not exceed MaxPinWidthUm (got {MinPinWidthUm} > {MaxPinWidthUm}).",
                nameof(MinPinWidthUm));
        }
    }
}
