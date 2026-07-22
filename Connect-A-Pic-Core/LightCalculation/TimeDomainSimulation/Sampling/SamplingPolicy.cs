namespace CAP_Core.LightCalculation.TimeDomainSimulation.Sampling;

/// <summary>
/// Derives the signal-driven time grid for sample-mode transient simulation
/// (issue #600): the sample rate follows the data signal
/// (bitrate × samples-per-symbol), not the optical sweep bandwidth, so data
/// waveforms at realistic bit rates are representable.
/// </summary>
public static class SamplingPolicy
{
    /// <summary>
    /// Minimum samples per symbol, enforced for anti-aliasing headroom
    /// (design doc #600, decision D4).
    /// </summary>
    public const int MinSamplesPerSymbol = 16;

    /// <summary>
    /// Creates the time grid for a data run:
    /// sample rate = <paramref name="bitrateHz"/> × <paramref name="samplesPerSymbol"/>,
    /// sample count = <paramref name="samplesPerSymbol"/> × <paramref name="symbolCount"/>
    /// + <paramref name="guardSamples"/>.
    /// </summary>
    /// <param name="bitrateHz">Symbol/bit rate in Hz.</param>
    /// <param name="samplesPerSymbol">
    /// Samples per symbol (≥ <see cref="MinSamplesPerSymbol"/>).
    /// </param>
    /// <param name="symbolCount">Number of symbols in the run.</param>
    /// <param name="guardSamples">
    /// Settle/guard tail appended after the last symbol; choose ≥ the impulse
    /// response length so the convolution tail is not truncated.
    /// </param>
    public static TimeSignalDefinition CreateGrid(
        double bitrateHz, int samplesPerSymbol, int symbolCount, int guardSamples)
    {
        if (bitrateHz <= 0) throw new ArgumentOutOfRangeException(nameof(bitrateHz));
        if (samplesPerSymbol < MinSamplesPerSymbol)
            throw new ArgumentOutOfRangeException(
                nameof(samplesPerSymbol),
                $"samplesPerSymbol must be ≥ {MinSamplesPerSymbol} for anti-aliasing headroom.");
        if (symbolCount <= 0) throw new ArgumentOutOfRangeException(nameof(symbolCount));
        if (guardSamples < 0) throw new ArgumentOutOfRangeException(nameof(guardSamples));

        return new TimeSignalDefinition(
            bitrateHz * samplesPerSymbol,
            samplesPerSymbol * symbolCount + guardSamples);
    }
}
