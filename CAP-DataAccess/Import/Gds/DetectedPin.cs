namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// How a <see cref="DetectedPin"/> was found by <see cref="GdsPinDetector"/>.
/// </summary>
public enum DetectedPinSource
{
    /// <summary>Derived from a TEXT label on a configured port layer; the label text is the pin name.</summary>
    Label,

    /// <summary>Derived from a waveguide polygon edge touching the cell bounding box; unnamed until numbered.</summary>
    EdgeHeuristic,
}

/// <summary>
/// A pin detected on a flattened GDS cell. All values are already converted to
/// the application's coordinate convention: micrometers, Y axis pointing DOWN,
/// origin at the top-left corner of the cell bounding box. Angles follow the
/// application convention (direction = (cos θ, sin θ) in the Y-down plane):
/// 0° = east (outward on the right edge), 90° = down (bottom edge),
/// 180° = west (left edge), 270° = up (top edge).
/// </summary>
public sealed record DetectedPin
{
    /// <summary>Pin name: the label text for label pins, or <c>heur_N</c> for heuristic pins.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>X position in micrometers, app space (0 = left edge of the cell bounding box).</summary>
    public double XUm { get; init; }

    /// <summary>Y position in micrometers, app space (0 = TOP edge of the cell bounding box, Y grows downward).</summary>
    public double YUm { get; init; }

    /// <summary>Outward direction in degrees, app convention (see class summary).</summary>
    public double AngleDegrees { get; init; }

    /// <summary>Pin width in micrometers; 0 when unknown (e.g. pins derived from labels only).</summary>
    public double WidthUm { get; init; }

    /// <summary>How this pin was detected.</summary>
    public DetectedPinSource Source { get; init; }

    /// <summary>
    /// Signal-domain knowledge: <c>true</c>/<c>false</c> when the kind is
    /// authoritative (pins of a known PDK component carry their template's
    /// kind), <c>null</c> when the kind is unknown (geometry-detected pins —
    /// a TEXT label or waveguide edge says nothing about the signal domain).
    /// The metal-route matcher infers kinds for unknown pins that participate
    /// in a metal-layer connection (metal only carries electrical signals).
    /// </summary>
    public bool? IsElectrical { get; init; }
}
