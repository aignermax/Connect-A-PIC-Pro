using MathNet.Numerics.Distributions;

namespace CAP_Core.LightCalculation.TimeDomainSimulation.PhaseNoise;

/// <summary>
/// Generates the Wiener phase random walk of a laser with Lorentzian linewidth Δν
/// (issue #834): per time step, dφ ~ N(0, σ²) with σ² = 2π·Δν·dt, so the phase
/// variance grows linearly as Var[φ(t)] = 2π·Δν·t and the field autocorrelation
/// decays as E[e^{i(φ(t+τ)−φ(t))}] = e^{−π·Δν·τ} — the time-domain picture of a
/// Lorentzian line that the CW spectral average (#827) cannot capture.
/// </summary>
public static class WienerPhaseProcess
{
    /// <summary>
    /// Generates the phase samples φ[0..n−1] of one Wiener walk starting at φ[0] = 0.
    /// A linewidth of zero (or less) yields the all-zero phase of an ideal laser.
    /// </summary>
    /// <param name="linewidthHz">Lorentzian FWHM linewidth Δν in Hz (≤ 0 = ideal).</param>
    /// <param name="dtSeconds">Time step in seconds (must be positive).</param>
    /// <param name="nSamples">Number of samples (must be positive).</param>
    /// <param name="rng">Random source; supply a seeded instance for reproducible runs.</param>
    public static double[] Generate(double linewidthHz, double dtSeconds, int nSamples, Random rng)
    {
        if (rng == null) throw new ArgumentNullException(nameof(rng));
        if (dtSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(dtSeconds));
        if (nSamples <= 0) throw new ArgumentOutOfRangeException(nameof(nSamples));

        var phases = new double[nSamples];
        if (linewidthHz <= 0)
            return phases;

        double sigma = Math.Sqrt(2 * Math.PI * linewidthHz * dtSeconds);
        for (int n = 1; n < nSamples; n++)
            phases[n] = phases[n - 1] + Normal.Sample(rng, 0, sigma);
        return phases;
    }

    /// <summary>Coherence time τc = 1/(π·Δν); infinite for an ideal (zero-linewidth) laser.</summary>
    /// <param name="linewidthHz">Lorentzian FWHM linewidth Δν in Hz.</param>
    public static double CoherenceTimeSeconds(double linewidthHz)
        => linewidthHz <= 0 ? double.PositiveInfinity : 1.0 / (Math.PI * linewidthHz);
}
