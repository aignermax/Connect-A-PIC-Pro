namespace CAP_Core.Components.Core
{
    /// <summary>
    /// Polarization mode carried by an optical pin. Parallel domain to
    /// <see cref="MatterType"/>: every light pin carries exactly one
    /// polarization kind, and connections must be polarization-compatible.
    /// </summary>
    public enum PolarizationKind
    {
        /// <summary>Transverse-electric polarized single-mode light (the historical implicit default).</summary>
        TE,

        /// <summary>Transverse-magnetic polarized single-mode light.</summary>
        TM,

        /// <summary>
        /// Polarization-agnostic pin that accepts and emits both TE and TM,
        /// e.g. polarization rotators, splitters, or 2D grating couplers.
        /// A <see cref="Both"/> pin may connect to any other pin.
        /// </summary>
        Both
    }
}
