namespace CAP_Core.Analysis.EyeDiagram;

/// <summary>
/// Estimates Q-factor and bit-error rate from a folded time-domain trace.
/// At each time offset within the bit period the samples are split by the
/// decision threshold into mark/space populations; Q = (μ₁ − μ₀)/(σ₁ + σ₀)
/// and BER = ½·erfc(Q/√2). The optimal sampling instant is where Q peaks.
/// </summary>
public static class BerEstimator
{
    /// <summary>Q at which the eye is considered "open" (BER ≈ 1e-3); used for the eye-width metric.</summary>
    public const double MinOpenQFactor = 3.09;

    /// <summary>Cap for reported Q when the populations are noise-free (avoids Infinity in the UI).</summary>
    public const double MaxReportedQFactor = 1000;

    /// <summary>Minimum samples per level (mark/space) for a statistically usable time bin.</summary>
    private const int MinSamplesPerLevel = 3;

    /// <summary>
    /// Estimates the eye metrics of <paramref name="trace"/> folded at <paramref name="bitPeriodSeconds"/>.
    /// </summary>
    /// <param name="trace">Intensity trace |E(t)|² from the transient simulation.</param>
    /// <param name="sampleRateHz">Sample rate of the trace in Hz.</param>
    /// <param name="bitPeriodSeconds">Bit period in seconds.</param>
    /// <param name="decisionThreshold">Decision threshold in trace amplitude units.</param>
    /// <param name="noise">Optional receiver noise added in quadrature to the measured spread.</param>
    /// <param name="timeBins">Number of time offsets evaluated within the bit period.</param>
    /// <param name="skipBits">Leading bits skipped (convolution transient).</param>
    /// <returns>Metrics at the optimal sampling instant; a closed eye yields Q = 0 and BER = 0.5.</returns>
    public static EyeMetrics Estimate(
        double[] trace,
        double sampleRateHz,
        double bitPeriodSeconds,
        double decisionThreshold,
        NoiseModel? noise = null,
        int timeBins = EyeDiagramBuilder.DefaultTimeBins,
        int skipBits = EyeDiagramBuilder.DefaultSkipBits)
    {
        if (trace == null) throw new ArgumentNullException(nameof(trace));
        if (sampleRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        if (bitPeriodSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(bitPeriodSeconds));
        if (timeBins < 1) throw new ArgumentOutOfRangeException(nameof(timeBins));

        double dt = 1.0 / sampleRateHz;
        int startSample = Math.Min((int)(skipBits * bitPeriodSeconds * sampleRateHz), trace.Length);

        var bins = FoldIntoBins(trace, startSample, dt, bitPeriodSeconds, timeBins);
        var qPerBin = bins.Select(b => BinQFactor(b, decisionThreshold, noise)).ToArray();

        int bestBin = ArgMax(qPerBin);
        double bestQ = qPerBin[bestBin];
        if (bestQ <= 0)
            return new EyeMetrics(0, 0.5, 0, 0, 0, 0);

        double binWidth = bitPeriodSeconds / timeBins;
        double eyeWidth = qPerBin.Count(q => q >= MinOpenQFactor) * binWidth;
        double eyeHeight = EyeHeightAt(bins[bestBin], decisionThreshold, noise);
        double jitter = RmsCrossingJitter(trace, startSample, dt, bitPeriodSeconds, decisionThreshold);
        double ber = 0.5 * Erfc(bestQ / Math.Sqrt(2));

        return new EyeMetrics(bestQ, ber, eyeHeight, eyeWidth, jitter, (bestBin + 0.5) * binWidth);
    }

    private static List<double>[] FoldIntoBins(
        double[] trace, int startSample, double dt, double bitPeriodSeconds, int timeBins)
    {
        var bins = new List<double>[timeBins];
        for (int i = 0; i < timeBins; i++) bins[i] = new List<double>();

        for (int n = startSample; n < trace.Length; n++)
        {
            double offset = (n * dt) % bitPeriodSeconds;
            int bin = Math.Min((int)(offset / bitPeriodSeconds * timeBins), timeBins - 1);
            bins[bin].Add(trace[n]);
        }
        return bins;
    }

    /// <summary>Q of a single time bin, or 0 if either level has too few samples.</summary>
    private static double BinQFactor(List<double> samples, double threshold, NoiseModel? noise)
    {
        var (ok, mu0, sigma0, mu1, sigma1) = LevelStatistics(samples, threshold, noise);
        if (!ok) return 0;

        double sigmaSum = sigma0 + sigma1;
        if (sigmaSum <= 0) return MaxReportedQFactor;
        return Math.Min((mu1 - mu0) / sigmaSum, MaxReportedQFactor);
    }

    private static double EyeHeightAt(List<double> samples, double threshold, NoiseModel? noise)
    {
        const double SigmaMargin = 3.0;
        var (ok, mu0, sigma0, mu1, sigma1) = LevelStatistics(samples, threshold, noise);
        if (!ok) return 0;
        return (mu1 - SigmaMargin * sigma1) - (mu0 + SigmaMargin * sigma0);
    }

    /// <summary>Mark/space means and standard deviations (noise added in quadrature).</summary>
    private static (bool Ok, double Mu0, double Sigma0, double Mu1, double Sigma1) LevelStatistics(
        List<double> samples, double threshold, NoiseModel? noise)
    {
        var zeros = samples.Where(v => v < threshold).ToList();
        var ones = samples.Where(v => v >= threshold).ToList();
        if (zeros.Count < MinSamplesPerLevel || ones.Count < MinSamplesPerLevel)
            return (false, 0, 0, 0, 0);

        var (mu0, sigma0) = MeanAndStd(zeros);
        var (mu1, sigma1) = MeanAndStd(ones);

        if (noise != null)
        {
            sigma0 = Quadrature(sigma0, noise.TotalSigmaOpticalPower(mu0));
            sigma1 = Quadrature(sigma1, noise.TotalSigmaOpticalPower(mu1));
        }
        return (true, mu0, sigma0, mu1, sigma1);
    }

    /// <summary>
    /// RMS spread of the threshold-crossing instants around their mean position
    /// within the bit period (crossings are folded to [−T/2, T/2) about the boundary).
    /// </summary>
    private static double RmsCrossingJitter(
        double[] trace, int startSample, double dt, double bitPeriodSeconds, double threshold)
    {
        var offsets = new List<double>();
        for (int n = Math.Max(startSample, 1); n < trace.Length; n++)
        {
            double v0 = trace[n - 1], v1 = trace[n];
            if ((v0 - threshold) * (v1 - threshold) >= 0 || v0 == v1) continue;

            double crossing = ((n - 1) + (threshold - v0) / (v1 - v0)) * dt;
            double offset = crossing % bitPeriodSeconds;
            if (offset > bitPeriodSeconds / 2) offset -= bitPeriodSeconds;
            offsets.Add(offset);
        }
        if (offsets.Count < 2) return 0;

        double mean = offsets.Average();
        return Math.Sqrt(offsets.Average(o => (o - mean) * (o - mean)));
    }

    private static (double Mean, double Std) MeanAndStd(List<double> values)
    {
        double mean = values.Average();
        double variance = values.Average(v => (v - mean) * (v - mean));
        return (mean, Math.Sqrt(variance));
    }

    private static double Quadrature(double a, double b) => Math.Sqrt(a * a + b * b);

    private static int ArgMax(double[] values)
    {
        int best = 0;
        for (int i = 1; i < values.Length; i++)
            if (values[i] > values[best]) best = i;
        return best;
    }

    /// <summary>
    /// Complementary error function via the Chebyshev-fitted approximation of
    /// Numerical Recipes (erfcc); fractional accuracy better than 1.2e-7 for all x,
    /// which is adequate down to BER levels far below 1e-15.
    /// </summary>
    /// <param name="x">Argument.</param>
    public static double Erfc(double x)
    {
        double z = Math.Abs(x);
        double t = 1.0 / (1.0 + 0.5 * z);
        double ans = t * Math.Exp(-z * z - 1.26551223 +
            t * (1.00002368 + t * (0.37409196 + t * (0.09678418 +
            t * (-0.18628806 + t * (0.27886807 + t * (-1.13520398 +
            t * (1.48851587 + t * (-0.82215223 + t * 0.17087277)))))))));
        return x >= 0 ? ans : 2.0 - ans;
    }
}
