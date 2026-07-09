namespace CAP_Core.Solvers.ModeProbe;

/// <summary>
/// Estimates the mode-field diameter (MFD) of a guided waveguide mode from the
/// solved effective index. The field is approximated as the core size plus twice
/// the evanescent penetration depth into the cladding,
/// δ = λ / (2π·√(n_eff² − n_clad²)) — a standard first-order estimate that is
/// exact in the limit of a slab guide and sufficient for the Gaussian overlap
/// approximation used by <see cref="FiberOverlapCalculator"/>.
/// </summary>
public static class ModeFieldEstimator
{
    /// <summary>
    /// Estimated horizontal and vertical mode-field diameters in µm, or null when
    /// the mode is not guided (n_eff ≤ n_clad) and no meaningful MFD exists.
    /// </summary>
    /// <param name="widthMicrometers">Core width in µm.</param>
    /// <param name="heightMicrometers">Core height in µm.</param>
    /// <param name="nEff">Solved effective index of the mode.</param>
    /// <param name="cladIndex">Cladding refractive index.</param>
    /// <param name="wavelengthMicrometers">Wavelength in µm.</param>
    public static (double MfdX, double MfdY)? EstimateMfd(
        double widthMicrometers,
        double heightMicrometers,
        double nEff,
        double cladIndex,
        double wavelengthMicrometers)
    {
        if (widthMicrometers <= 0 || heightMicrometers <= 0 || wavelengthMicrometers <= 0)
            return null;

        double indexContrast = nEff * nEff - cladIndex * cladIndex;
        if (indexContrast <= 0)
            return null; // unguided — field is not confined, MFD undefined

        double penetrationDepth = wavelengthMicrometers / (2.0 * Math.PI * Math.Sqrt(indexContrast));
        return (widthMicrometers + 2.0 * penetrationDepth,
                heightMicrometers + 2.0 * penetrationDepth);
    }
}
