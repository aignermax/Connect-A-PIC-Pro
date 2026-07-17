namespace CAP_Core.Routing.InterconnectRouting;

/// <summary>
/// Global interconnect (waveguide routing) settings used as defaults for new
/// connections and for the Nazca export header.
/// Values default to the historical hardcoded export values so existing
/// designs keep producing identical GDS output.
/// </summary>
public class InterconnectSettings
{
    /// <summary>Default waveguide width in micrometers (Nazca WG_WIDTH).</summary>
    public const double DefaultWidthMicrometers = 0.45;

    /// <summary>Default bend radius in micrometers (Nazca BEND_RADIUS).</summary>
    public const double DefaultBendRadiusMicrometers = 50.0;

    /// <summary>Waveguide width in micrometers used for interconnects.</summary>
    public double WidthMicrometers { get; set; } = DefaultWidthMicrometers;

    /// <summary>Default bend radius in micrometers used for interconnects.</summary>
    public double BendRadiusMicrometers { get; set; } = DefaultBendRadiusMicrometers;

    /// <summary>
    /// Optional GDS layer for interconnects. Null means the PDK/Nazca default layer is used.
    /// </summary>
    public int? GdsLayer { get; set; }

    /// <summary>Creates a copy of these settings.</summary>
    public InterconnectSettings Clone() => new()
    {
        WidthMicrometers = WidthMicrometers,
        BendRadiusMicrometers = BendRadiusMicrometers,
        GdsLayer = GdsLayer,
    };
}
