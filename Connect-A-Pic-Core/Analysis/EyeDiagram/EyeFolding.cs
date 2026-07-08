namespace CAP_Core.Analysis.EyeDiagram;

/// <summary>
/// Shared time-domain folding math used by <see cref="EyeDiagramBuilder"/> and
/// <see cref="BerEstimator"/> so both fold a trace at the bit-period boundary
/// identically. Pure de-duplication — no behavior differs from the previous
/// per-class implementations.
/// </summary>
internal static class EyeFolding
{
    /// <summary>
    /// Index of the first trace sample to include after skipping
    /// <paramref name="skipBits"/> leading bits (discards the convolution transient).
    /// </summary>
    /// <param name="skipBits">Leading bits to skip.</param>
    /// <param name="bitPeriodSeconds">Bit period in seconds.</param>
    /// <param name="sampleRateHz">Sample rate of the trace in Hz.</param>
    /// <param name="traceLength">Length of the trace, used as an upper bound.</param>
    public static int StartSample(int skipBits, double bitPeriodSeconds, double sampleRateHz, int traceLength)
        => Math.Min((int)(skipBits * bitPeriodSeconds * sampleRateHz), traceLength);

    /// <summary>
    /// Time-bin index for sample index <paramref name="sampleIndex"/> when the trace is
    /// folded at <paramref name="bitPeriodSeconds"/> into <paramref name="timeBins"/> bins.
    /// </summary>
    /// <param name="sampleIndex">Absolute sample index in the trace.</param>
    /// <param name="dt">Sample period in seconds (1 / sample rate).</param>
    /// <param name="bitPeriodSeconds">Bit period in seconds.</param>
    /// <param name="timeBins">Number of time bins per bit period.</param>
    public static int TimeBin(int sampleIndex, double dt, double bitPeriodSeconds, int timeBins)
    {
        double offset = (sampleIndex * dt) % bitPeriodSeconds;
        return Math.Min((int)(offset / bitPeriodSeconds * timeBins), timeBins - 1);
    }
}
