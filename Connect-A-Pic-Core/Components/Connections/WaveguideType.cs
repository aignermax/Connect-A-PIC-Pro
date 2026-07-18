namespace CAP_Core.Components.Connections
{
    /// <summary>
    /// Routing style of a waveguide connection, mapped to Nazca primitives on export.
    /// Declaration order is the display order of the routing-style dropdown.
    /// Legacy saved styles "Straight" and "Euler" are migrated on load
    /// (see <c>FileOperationsViewModel.RestoreRoutingSettings</c>).
    /// </summary>
    public enum WaveguideType
    {
        /// <summary>Automatic routing (A* segments, sbend_p2p fallback).</summary>
        Auto,

        /// <summary>Single circular arc (nd.bend).</summary>
        Bend,

        /// <summary>S-curve (nd.sinebend).</summary>
        SBend,

        /// <summary>Cobra point-to-point curve (nd.cobra).</summary>
        Cobra,
    }
}
