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
    /// (Layer, Datatype) pairs whose polygons count as METAL (electrical): they
    /// join <see cref="WaveguideLayers"/> for the label-pin direction rule, and
    /// a label anchor touching one proves the pin ELECTRICAL (metal only carries
    /// electrical signals — the layer-based kind inference). A pair listed in
    /// BOTH this and <see cref="WaveguideLayers"/> counts as metal (the stronger
    /// evidence wins).
    ///
    /// Default: null = AUTO. Foundry layer tables assign numbers freely, so a
    /// hardcoded metal default misreads foreign files (a real foundry file's
    /// optical routes on (12, 0) imported as electrical). The hierarchy
    /// importer resolves AUTO via the Lunima export sentinel
    /// (<see cref="GdsOwnExportSentinel"/>): our own exports get
    /// <see cref="LunimaElectricalLayers"/>, foreign files get NONE until the
    /// user supplies a mapping. Standalone <see cref="GdsPinDetector"/> calls
    /// (no library context) fall back to <see cref="LunimaElectricalLayers"/>.
    /// </summary>
    public IReadOnlyList<(int Layer, int Datatype)>? ElectricalLayers { get; init; }

    /// <summary>
    /// The metal layers OUR OWN exporters write: (11, 0) and (12, 0), the metal
    /// trace and bridge-marker layers (<c>MetalRoutingSpec</c> defaults,
    /// mirrored by <see cref="GdsHierarchyImportOptions.LunimaMetalRouteLayers"/>),
    /// plus (13, 0), SiEPIC's PAD_OPEN bond-pad layer (siepic-ebeam-pdk.json).
    /// Applied for <see cref="ElectricalLayers"/> = AUTO on recognizably-own
    /// exports only.
    /// </summary>
    public static readonly IReadOnlyList<(int Layer, int Datatype)> LunimaElectricalLayers =
        [(11, 0), (12, 0), (13, 0)];

    /// <summary>
    /// Distance in micrometers within which a segment endpoint or text anchor is
    /// considered to lie on a bounding-box edge line. Default: 0.001 µm = 1 nm
    /// (one database unit in a typical 1 nm grid).
    /// </summary>
    public double EdgeTouchToleranceUm { get; init; } = 0.001;

    /// <summary>
    /// Distance in micrometers within which a label anchor is considered to lie
    /// ON a waveguide/metal polygon's outline — the window for the segment-normal
    /// direction rule and the layer-based kind inference. Deliberately wider than
    /// <see cref="EdgeTouchToleranceUm"/>: port labels sit exactly on their
    /// geometry in a clean export, but PDK cell swaps and grid rounding shift
    /// them by fractions of a micron (the same reasoning as the route matcher's
    /// pin-touch tolerance, <see cref="GdsHierarchyImportOptions.PinTouchToleranceUm"/>,
    /// also 1.0 µm). Default: 1.0 µm.
    /// </summary>
    public double LabelGeometryTouchToleranceUm { get; init; } = 1.0;

    /// <summary>Heuristic pins narrower than this (µm) are discarded as spurious touches. Default: 0.1.</summary>
    public double MinPinWidthUm { get; init; } = 0.1;

    /// <summary>Heuristic pins wider than this (µm) are discarded as slab/boundary contacts. Default: 100.</summary>
    public double MaxPinWidthUm { get; init; } = 100.0;

    /// <summary>
    /// Throws when the width window is inconsistent (<see cref="MinPinWidthUm"/>
    /// above <see cref="MaxPinWidthUm"/>) or a tolerance is negative. Called by
    /// <see cref="GdsPinDetector"/> before detection.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A tolerance is negative.</exception>
    /// <exception cref="ArgumentException">The pin-width window is inverted.</exception>
    public void Validate()
    {
        if (EdgeTouchToleranceUm < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(EdgeTouchToleranceUm), EdgeTouchToleranceUm, "The edge-touch tolerance must be ≥ 0.");
        }
        if (LabelGeometryTouchToleranceUm < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(LabelGeometryTouchToleranceUm), LabelGeometryTouchToleranceUm,
                "The label-geometry touch tolerance must be ≥ 0.");
        }
        if (MinPinWidthUm > MaxPinWidthUm)
        {
            throw new ArgumentException(
                $"MinPinWidthUm must not exceed MaxPinWidthUm (got {MinPinWidthUm} > {MaxPinWidthUm}).",
                nameof(MinPinWidthUm));
        }
    }
}
