namespace CAP_DataAccess.Import.Gds.LayerCensus;

/// <summary>
/// Layer-number conventions of tools and PDKs this codebase already knows
/// (mirroring the documented defaults of <see cref="GdsPinDetectionOptions"/>
/// and <see cref="GdsHierarchyImportOptions"/>). A census pair matching one of
/// these becomes a high-confidence suggestion whose reason names the source —
/// per-PDK tables from the process model are a planned extension.
/// </summary>
internal static class GdsKnownLayerTables
{
    /// <summary>One known (layer, datatype) → role convention with its named source.</summary>
    internal sealed record KnownLayer(int Layer, int Datatype, GdsLayerRole Role, string Source);

    private static readonly KnownLayer[] Entries =
    {
        new(1, 10, GdsLayerRole.PortLabels, "gdsfactory PORT / SiEPIC PinRec"),
        new(1, 0, GdsLayerRole.Waveguide, "gdsfactory WG core / SiEPIC Si"),
        new(501, 1, GdsLayerRole.PortLabels, "nazca demofab bb_pin_text"),
        new(1111, 0, GdsLayerRole.Waveguide, "nazca interconnect"),
        new(3, 0, GdsLayerRole.Waveguide, "CORNERSTONE Si core"),
        new(11, 0, GdsLayerRole.Metal, "Lunima metal trace / SiEPIC M1"),
        new(12, 0, GdsLayerRole.Metal, "Lunima bridge marker / SiEPIC M2 router"),
        new(13, 0, GdsLayerRole.Metal, "SiEPIC PAD_OPEN"),
        new(41, 0, GdsLayerRole.Metal, "gdsfactory generic M1"),
        new(45, 0, GdsLayerRole.Metal, "gdsfactory generic M2"),
        new(49, 0, GdsLayerRole.Metal, "gdsfactory generic M3"),
    };

    /// <summary>All known conventions for the given (layer, datatype) pair.</summary>
    internal static IEnumerable<KnownLayer> Match(int layer, int datatype) =>
        Entries.Where(e => e.Layer == layer && e.Datatype == datatype);
}
