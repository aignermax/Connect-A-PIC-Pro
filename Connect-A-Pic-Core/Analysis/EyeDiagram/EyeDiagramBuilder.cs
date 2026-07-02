namespace CAP_Core.Analysis.EyeDiagram;

/// <summary>
/// Folds a time-domain intensity trace at the bit-period boundary and
/// accumulates the overlaid slices into a 2D persistence histogram
/// (time offset within bit period × amplitude).
/// </summary>
public static class EyeDiagramBuilder
{
    /// <summary>Default number of time bins per bit period.</summary>
    public const int DefaultTimeBins = 64;

    /// <summary>Default number of amplitude bins.</summary>
    public const int DefaultAmplitudeBins = 64;

    /// <summary>Bits skipped at the trace start to discard the convolution transient.</summary>
    public const int DefaultSkipBits = 2;

    /// <summary>
    /// Builds the eye histogram from a trace sampled at <paramref name="sampleRateHz"/>.
    /// </summary>
    /// <param name="trace">Intensity trace |E(t)|² from the transient simulation.</param>
    /// <param name="sampleRateHz">Sample rate of the trace in Hz.</param>
    /// <param name="bitPeriodSeconds">Bit period to fold at (seconds).</param>
    /// <param name="timeBins">Number of time bins per bit period.</param>
    /// <param name="amplitudeBins">Number of amplitude bins.</param>
    /// <param name="skipBits">Leading bits to skip (convolution transient).</param>
    public static EyeHistogram Build(
        double[] trace,
        double sampleRateHz,
        double bitPeriodSeconds,
        int timeBins = DefaultTimeBins,
        int amplitudeBins = DefaultAmplitudeBins,
        int skipBits = DefaultSkipBits)
    {
        ValidateArguments(trace, sampleRateHz, bitPeriodSeconds, timeBins, amplitudeBins);

        double dt = 1.0 / sampleRateHz;
        int startSample = Math.Min((int)(skipBits * bitPeriodSeconds * sampleRateHz), trace.Length);

        (double min, double max) = AmplitudeRange(trace, startSample);
        double ampSpan = max - min;
        var counts = new int[timeBins, amplitudeBins];

        for (int n = startSample; n < trace.Length; n++)
        {
            double offset = (n * dt) % bitPeriodSeconds;
            int timeBin = Math.Min((int)(offset / bitPeriodSeconds * timeBins), timeBins - 1);

            int ampBin = ampSpan <= 0
                ? 0
                : Math.Min((int)((trace[n] - min) / ampSpan * amplitudeBins), amplitudeBins - 1);

            counts[timeBin, ampBin]++;
        }

        return new EyeHistogram(counts, bitPeriodSeconds, min, max);
    }

    private static void ValidateArguments(
        double[] trace, double sampleRateHz, double bitPeriodSeconds, int timeBins, int amplitudeBins)
    {
        if (trace == null) throw new ArgumentNullException(nameof(trace));
        if (trace.Length == 0) throw new ArgumentException("Trace must not be empty.", nameof(trace));
        if (sampleRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        if (bitPeriodSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(bitPeriodSeconds));
        if (timeBins < 1) throw new ArgumentOutOfRangeException(nameof(timeBins));
        if (amplitudeBins < 1) throw new ArgumentOutOfRangeException(nameof(amplitudeBins));
    }

    private static (double Min, double Max) AmplitudeRange(double[] trace, int startSample)
    {
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        for (int n = startSample; n < trace.Length; n++)
        {
            if (trace[n] < min) min = trace[n];
            if (trace[n] > max) max = trace[n];
        }
        if (double.IsInfinity(min)) { min = 0; max = 0; }
        return (min, max);
    }
}
