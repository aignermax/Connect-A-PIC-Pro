using System.Globalization;

namespace CAP_Core.Export;

/// <summary>
/// The geometry an electrical (metal) routing trace is drawn with in a GDS export:
/// its width and the GDS layer/datatype it lands on. Electrical connections are NOT
/// optical waveguides — they must not be emitted on the waveguide layer (issue #682).
/// A fabrication process declares this via a metal cross-section; when none is defined
/// the exporter falls back to <see cref="Default"/> so the trace is still drawn on a
/// distinct, clearly-metal layer rather than silently becoming an optical waveguide.
/// </summary>
public sealed record MetalTraceStyle
{
    /// <summary>Trace width in micrometres.</summary>
    public double WidthUm { get; init; }

    /// <summary>GDS layer number the metal trace is drawn on.</summary>
    public int GdsLayer { get; init; }

    /// <summary>GDS datatype of the metal layer.</summary>
    public int GdsDatatype { get; init; }

    /// <summary>Default metal trace width in µm when the process defines no metal cross-section.</summary>
    public const double DefaultWidthUm = 2.0;

    /// <summary>
    /// Default metal GDS layer when the process defines none. Chosen distinct from the
    /// waveguide layer (1) so a placeholder metal trace is never mistaken for an optical
    /// one; a real process should override this via its metal cross-section.
    /// </summary>
    public const int DefaultGdsLayer = 11;

    /// <summary>
    /// Fallback style used when the active process declares no metal cross-section:
    /// a 2 µm trace on layer 11/0. Overridden by a process's metal cross-section.
    /// </summary>
    public static MetalTraceStyle Default { get; } = new()
    {
        WidthUm = DefaultWidthUm,
        GdsLayer = DefaultGdsLayer,
        GdsDatatype = 0,
    };

    /// <summary>The <c>(layer, datatype)</c> tuple literal used in Nazca/gdsfactory scripts.</summary>
    public string LayerTuple =>
        $"({GdsLayer.ToString(CultureInfo.InvariantCulture)}, {GdsDatatype.ToString(CultureInfo.InvariantCulture)})";

    /// <summary>The width formatted for a machine-facing export script (invariant culture).</summary>
    public string WidthLiteral => WidthUm.ToString("F2", CultureInfo.InvariantCulture);
}
