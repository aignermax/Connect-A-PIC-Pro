namespace CAP_Core.Analysis.EyeDiagram;

/// <summary>
/// Applies the additive receiver-noise model (shot + thermal + laser RIN) to
/// an intensity trace for the eye-diagram PLOT. The metrics path already
/// accounted for this noise analytically (BER estimator, in quadrature); the
/// histogram used to be built from the clean trace, so RIN changes were
/// invisible in the plot — a field report. Per-sample σ comes from
/// <see cref="NoiseModel.TotalSigmaOpticalPower"/> at the sample's own power,
/// so RIN-heavy settings visibly widen the mark level more than the space.
/// Deterministic by seed: repeated runs (and UI screenshots) stay identical.
/// </summary>
public static class ReceiverNoiseTrace
{
    /// <summary>Returns a noisified copy of <paramref name="trace"/> (input stays untouched).</summary>
    public static double[] Apply(double[] trace, NoiseModel noise, int seed = 42)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(noise);
        var rng = new Random(seed);
        var result = new double[trace.Length];
        for (int i = 0; i < trace.Length; i++)
        {
            // optical power cannot go negative
            result[i] = Math.Max(0, trace[i] + NextGaussian(rng) * noise.TotalSigmaOpticalPower(trace[i]));
        }
        return result;
    }

    /// <summary>Standard-normal sample via Box–Muller.</summary>
    private static double NextGaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
