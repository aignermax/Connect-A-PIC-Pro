namespace CAP_Core.Analysis.EyeDiagram;

/// <summary>
/// Maps a user-chosen bit rate onto the fixed sample grid of a transient
/// simulation: how many samples per bit, how many bits fit within the sample
/// budget, and the resulting (grid-aligned) bit period.
/// </summary>
public class EyeSimulationPlan
{
    /// <summary>Fewest samples per bit for a meaningful eye (below this the bit rate exceeds the simulated bandwidth).</summary>
    public const int MinSamplesPerBit = 4;

    /// <summary>Fewest bits required for meaningful eye statistics.</summary>
    public const int MinBits = 16;

    /// <summary>Upper bound on total samples so pattern length × oversampling stays tractable (2^20).</summary>
    public const int MaxTotalSamples = 1 << 20;

    /// <summary>Samples per bit period (integer, so bit boundaries align with the sample grid).</summary>
    public int SamplesPerBit { get; }

    /// <summary>Number of bits actually simulated (pattern may be truncated to fit the sample budget).</summary>
    public int BitCount { get; }

    /// <summary>Total samples = <see cref="BitCount"/> × <see cref="SamplesPerBit"/>.</summary>
    public int TotalSamples => BitCount * SamplesPerBit;

    /// <summary>Grid-aligned bit period in seconds = SamplesPerBit / sample rate.</summary>
    public double BitPeriodSeconds { get; }

    private EyeSimulationPlan(int samplesPerBit, int bitCount, double bitPeriodSeconds)
    {
        SamplesPerBit = samplesPerBit;
        BitCount = bitCount;
        BitPeriodSeconds = bitPeriodSeconds;
    }

    /// <summary>
    /// Creates a plan for the given bit rate on a sample grid of <paramref name="sampleRateHz"/>.
    /// </summary>
    /// <param name="bitRateHz">Target bit rate in bit/s.</param>
    /// <param name="sampleRateHz">Simulation sample rate in Hz (from the wavelength sweep bandwidth).</param>
    /// <param name="patternBits">Full PRBS pattern length; truncated if it exceeds the sample budget.</param>
    /// <exception cref="InvalidOperationException">
    /// If the bit rate is too high (fewer than <see cref="MinSamplesPerBit"/> samples per bit)
    /// or too low (fewer than <see cref="MinBits"/> bits fit into <see cref="MaxTotalSamples"/>).
    /// </exception>
    public static EyeSimulationPlan Create(double bitRateHz, double sampleRateHz, int patternBits)
    {
        if (bitRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(bitRateHz));
        if (sampleRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        if (patternBits <= 0) throw new ArgumentOutOfRangeException(nameof(patternBits));

        double idealSamplesPerBit = sampleRateHz / bitRateHz;
        int samplesPerBit = (int)Math.Round(idealSamplesPerBit);

        if (samplesPerBit < MinSamplesPerBit)
            throw new InvalidOperationException(
                $"Bit rate too high: only {idealSamplesPerBit:F1} samples per bit at the simulated bandwidth " +
                $"(need ≥ {MinSamplesPerBit}). Lower the bit rate or widen the wavelength span.");

        int bitCount = Math.Min(patternBits, MaxTotalSamples / samplesPerBit);
        if (bitCount < MinBits)
            throw new InvalidOperationException(
                $"Bit rate too low: only {bitCount} bits fit into the sample budget " +
                $"(need ≥ {MinBits}). Increase the bit rate.");

        return new EyeSimulationPlan(samplesPerBit, bitCount, samplesPerBit / sampleRateHz);
    }
}
