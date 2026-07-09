namespace CAP_Core.Solvers.ModeProbe;

/// <summary>
/// Result of a fiber-mode overlap computation.
/// </summary>
/// <param name="Efficiency">Power coupling efficiency in [0, 1].</param>
public sealed record FiberOverlapResult(double Efficiency)
{
    /// <summary>Coupling efficiency in percent.</summary>
    public double EfficiencyPercent => Efficiency * 100.0;

    /// <summary>Coupling loss in dB (0 dB = perfect overlap; positive infinity for zero overlap).</summary>
    public double LossDb => Efficiency <= 0 ? double.PositiveInfinity : -10.0 * Math.Log10(Efficiency);
}

/// <summary>
/// Computes the power overlap integral between an elliptical Gaussian approximation
/// of the waveguide mode (mode-field diameters MFDx, MFDy) and a circular Gaussian
/// fiber mode (e.g. SMF-28: MFD ≈ 10.4 µm at 1550 nm). This is the number that
/// decides coupling loss at the chip edge, where the elliptical waveguide mode
/// meets the round fiber mode.
/// </summary>
public static class FiberOverlapCalculator
{
    /// <summary>Default fiber mode-field diameter in µm (SMF-28 at 1550 nm).</summary>
    public const double DefaultFiberMfdMicrometers = 10.4;

    /// <summary>
    /// Analytic Gaussian×Gaussian power overlap:
    /// η = [2·wx·wf / (wx² + wf²)] · [2·wy·wf / (wy² + wf²)]
    /// where w = MFD/2 are the 1/e² field radii. Equal spot sizes give η = 1.
    /// </summary>
    /// <param name="modeMfdXMicrometers">Waveguide mode-field diameter (horizontal) in µm.</param>
    /// <param name="modeMfdYMicrometers">Waveguide mode-field diameter (vertical) in µm.</param>
    /// <param name="fiberMfdMicrometers">Fiber mode-field diameter in µm.</param>
    /// <exception cref="ArgumentOutOfRangeException">Any diameter is not positive.</exception>
    public static FiberOverlapResult Compute(
        double modeMfdXMicrometers,
        double modeMfdYMicrometers,
        double fiberMfdMicrometers)
    {
        ThrowIfNotPositive(modeMfdXMicrometers, nameof(modeMfdXMicrometers));
        ThrowIfNotPositive(modeMfdYMicrometers, nameof(modeMfdYMicrometers));
        ThrowIfNotPositive(fiberMfdMicrometers, nameof(fiberMfdMicrometers));

        double wx = modeMfdXMicrometers / 2.0;
        double wy = modeMfdYMicrometers / 2.0;
        double wf = fiberMfdMicrometers / 2.0;

        double etaX = 2.0 * wx * wf / (wx * wx + wf * wf);
        double etaY = 2.0 * wy * wf / (wy * wy + wf * wf);
        return new FiberOverlapResult(etaX * etaY);
    }

    private static void ThrowIfNotPositive(double value, string name)
    {
        if (value <= 0 || double.IsNaN(value))
            throw new ArgumentOutOfRangeException(name, value, "Mode-field diameter must be positive.");
    }
}
