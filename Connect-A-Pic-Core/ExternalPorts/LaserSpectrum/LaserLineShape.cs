namespace CAP_Core.ExternalPorts.LaserSpectrum
{
    /// <summary>
    /// Spectral line shape of a laser source. <see cref="Ideal"/> is a single
    /// monochromatic wavelength (today's behaviour); the other shapes spread the
    /// emitted power over neighbouring wavelengths according to the linewidth.
    /// </summary>
    public enum LaserLineShape
    {
        /// <summary>Monochromatic source — all power at the center wavelength.</summary>
        Ideal,

        /// <summary>Gaussian line shape (inhomogeneously broadened source).</summary>
        Gaussian,

        /// <summary>Lorentzian line shape (homogeneously broadened source, e.g. DFB).</summary>
        Lorentzian,
    }
}
