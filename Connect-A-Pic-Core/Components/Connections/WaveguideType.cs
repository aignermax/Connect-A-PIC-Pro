namespace CAP_Core.Components.Connections
{
    /// <summary>
    /// Routing style of a waveguide connection, mapped to Nazca primitives on export.
    /// </summary>
    public enum WaveguideType
    {
        /// <summary>Automatic routing (A* segments, sbend_p2p fallback).</summary>
        Auto,

        /// <summary>Direct straight connection (nd.strt).</summary>
        Straight,

        /// <summary>S-curve (nd.sinebend).</summary>
        SBend,

        /// <summary>Single circular arc (nd.bend).</summary>
        Bend,

        /// <summary>Euler bend with adiabatic curvature (nd.euler).</summary>
        Euler,

        /// <summary>Cobra point-to-point curve (nd.cobra).</summary>
        Cobra,
    }
}
