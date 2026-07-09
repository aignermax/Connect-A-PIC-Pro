namespace CAP_Core.LightCalculation.TimeDomainSimulation.Sources;

/// <summary>
/// PRBS-NRZ data source (issue #600): a deterministic LFSR bit sequence
/// (<see cref="PrbsBitGenerator"/>) mapped to NRZ envelope levels, with
/// raised-cosine rise/fall shaping to band-limit the waveform below the
/// Nyquist frequency of the sampling grid.
/// </summary>
public class PrbsSource : ISignalSource
{
    /// <summary>Default rise/fall time as a fraction of the symbol duration.</summary>
    public const double DefaultRiseTimeFraction = 0.25;

    private readonly double _bitrateHz;
    private readonly int _prbsOrder;
    private readonly double _highLevel;
    private readonly double _lowLevel;
    private readonly int _seed;
    private readonly double _riseTimeFraction;

    /// <summary>Initializes a new PRBS-NRZ source.</summary>
    /// <param name="bitrateHz">Bit rate in bit/s (e.g. 25e9 for 25 Gbps).</param>
    /// <param name="prbsOrder">PRBS order (7, 9, 11, 15, 23 or 31).</param>
    /// <param name="highLevel">Envelope amplitude of a "1" bit (√W units).</param>
    /// <param name="extinctionRatioDb">
    /// Power extinction ratio P₁/P₀ in dB (must be positive); the "0" bit
    /// amplitude is <c>highLevel / √(10^(ER/10))</c>. Defaults to infinite
    /// extinction (a "0" bit is fully dark).
    /// </param>
    /// <param name="seed">LFSR seed for reproducible sequences.</param>
    /// <param name="riseTimeFraction">
    /// Raised-cosine edge duration as a fraction of the symbol duration
    /// (0 disables shaping and yields ideal rectangular NRZ).
    /// </param>
    public PrbsSource(
        double bitrateHz,
        int prbsOrder,
        double highLevel = 1.0,
        double extinctionRatioDb = double.PositiveInfinity,
        int seed = 1,
        double riseTimeFraction = DefaultRiseTimeFraction)
    {
        if (bitrateHz <= 0) throw new ArgumentOutOfRangeException(nameof(bitrateHz));
        if (highLevel < 0) throw new ArgumentOutOfRangeException(nameof(highLevel));
        if (extinctionRatioDb <= 0) throw new ArgumentOutOfRangeException(nameof(extinctionRatioDb));
        if (riseTimeFraction is < 0 or >= 0.5)
            throw new ArgumentOutOfRangeException(nameof(riseTimeFraction));

        _bitrateHz = bitrateHz;
        _prbsOrder = prbsOrder;
        _highLevel = highLevel;
        _lowLevel = double.IsPositiveInfinity(extinctionRatioDb)
            ? 0.0
            : highLevel / Math.Sqrt(Math.Pow(10.0, extinctionRatioDb / 10.0));
        _seed = seed;
        _riseTimeFraction = riseTimeFraction;
    }

    /// <inheritdoc/>
    public SignalDomain Domain => SignalDomain.Optical;

    /// <inheritdoc/>
    public double[] Generate(TimeSignalDefinition grid)
    {
        ArgumentNullException.ThrowIfNull(grid);

        double samplesPerBit = grid.SampleRateHz / _bitrateHz;
        int bitCount = (int)Math.Ceiling(grid.NSamples / samplesPerBit) + 1;
        var bits = PrbsBitGenerator.Generate(_prbsOrder, _seed, bitCount);

        var levels = BuildNrzStaircase(grid.NSamples, samplesPerBit, bits);
        ApplyRaisedCosineEdges(levels, samplesPerBit);
        return levels;
    }

    /// <summary>Maps each sample to its bit's NRZ level (ideal staircase).</summary>
    private double[] BuildNrzStaircase(int nSamples, double samplesPerBit, bool[] bits)
    {
        var levels = new double[nSamples];
        for (int n = 0; n < nSamples; n++)
        {
            int bitIndex = (int)(n / samplesPerBit);
            levels[n] = bits[bitIndex] ? _highLevel : _lowLevel;
        }
        return levels;
    }

    /// <summary>
    /// Replaces each level transition with a raised-cosine edge spanning
    /// <see cref="_riseTimeFraction"/> of a symbol, band-limiting the waveform.
    /// </summary>
    private void ApplyRaisedCosineEdges(double[] levels, double samplesPerBit)
    {
        int riseSamples = (int)Math.Round(_riseTimeFraction * samplesPerBit);
        if (riseSamples <= 0) return;

        var edges = new List<(int Start, double From, double To)>();
        for (int n = 1; n < levels.Length; n++)
            if (levels[n] != levels[n - 1]) edges.Add((n, levels[n - 1], levels[n]));

        foreach (var (start, from, to) in edges)
        {
            for (int j = 0; j < riseSamples && start + j < levels.Length; j++)
            {
                double progress = (j + 1.0) / riseSamples;
                double blend = 0.5 * (1.0 - Math.Cos(Math.PI * progress));
                levels[start + j] = from + (to - from) * blend;
            }
        }
    }
}
