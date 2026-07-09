using CAP_Core.Analysis.EyeDiagram;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.EyeDiagram;

public class BerEstimatorTests
{
    private const double SampleRate = 1e12;
    private const int SamplesPerBit = 16;
    private const double BitPeriod = SamplesPerBit / SampleRate;
    private const int TimeBins = 16;
    private const int BitCount = 127;

    /// <summary>PRBS-7 NRZ trace with seeded additive Gaussian noise (deterministic).</summary>
    private static double[] NoisyNrzTrace(double amplitude, double noiseSigma, int seed = 42)
    {
        var bits = PrbsGenerator.GenerateBits(PrbsOrder.Prbs7, BitCount);
        var samples = PrbsGenerator.ToNrzSamples(bits, SamplesPerBit, amplitude);
        var rng = new Random(seed);
        for (int i = 0; i < samples.Length; i++)
            samples[i] += NextGaussian(rng) * noiseSigma;
        return samples;
    }

    private static double NextGaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private static EyeMetrics Estimate(double[] trace, double threshold, NoiseModel? noise = null)
        => BerEstimator.Estimate(trace, SampleRate, BitPeriod, threshold, noise, TimeBins);

    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(1.0, 0.15729921)]
    [InlineData(-1.0, 1.84270079)]
    [InlineData(3.0, 2.209e-5)]
    public void Erfc_MatchesReferenceValues(double x, double expected)
    {
        BerEstimator.Erfc(x).ShouldBe(expected, Math.Max(Math.Abs(expected) * 1e-5, 1e-9));
    }

    [Fact]
    public void CleanEye_HasHighQAndNegligibleBer()
    {
        // Back-to-back link: clean two-level signal with tiny spread → BER ≪ 1e-12.
        var trace = NoisyNrzTrace(amplitude: 1.0, noiseSigma: 0.01);

        var metrics = Estimate(trace, threshold: 0.5);

        metrics.QFactor.ShouldBeGreaterThan(7.0);
        metrics.BerEstimate.ShouldBeLessThan(1e-12);
        metrics.EyeHeight.ShouldBeGreaterThan(0.5);
    }

    [Fact]
    public void Ber_IncreasesMonotonicallyWithAttenuation()
    {
        // Fixed receiver noise, shrinking signal → BER must rise monotonically.
        const double NoiseSigma = 0.05;
        var berByAmplitude = new[] { 1.0, 0.5, 0.25 }
            .Select(amp => Estimate(NoisyNrzTrace(amp, NoiseSigma), threshold: amp / 2).BerEstimate)
            .ToArray();

        berByAmplitude[1].ShouldBeGreaterThan(berByAmplitude[0]);
        berByAmplitude[2].ShouldBeGreaterThan(berByAmplitude[1]);
    }

    [Fact]
    public void NoiseModel_RaisesBerComparedToNoiselessEstimate()
    {
        var trace = NoisyNrzTrace(amplitude: 1.0, noiseSigma: 0.01);
        var heavyNoise = new NoiseModel
        {
            BandwidthHz = 18.75e9,
            RinDbPerHz = -100, // very noisy laser → RIN dominates
        };

        var without = Estimate(trace, threshold: 0.5);
        var with = Estimate(trace, threshold: 0.5, heavyNoise);

        with.QFactor.ShouldBeLessThan(without.QFactor);
        with.BerEstimate.ShouldBeGreaterThanOrEqualTo(without.BerEstimate);
    }

    [Fact]
    public void ClosedEye_ReturnsQZeroAndBerHalf()
    {
        var trace = Enumerable.Repeat(1.0, BitCount * SamplesPerBit).ToArray();

        var metrics = Estimate(trace, threshold: 0.5);

        metrics.QFactor.ShouldBe(0);
        metrics.BerEstimate.ShouldBe(0.5);
        metrics.EyeWidthSeconds.ShouldBe(0);
    }

    [Fact]
    public void CleanEye_IsOpenAcrossTheFullBitPeriod()
    {
        var trace = NoisyNrzTrace(amplitude: 1.0, noiseSigma: 0.01);

        var metrics = Estimate(trace, threshold: 0.5);

        metrics.EyeWidthSeconds.ShouldBeGreaterThanOrEqualTo(0.9 * BitPeriod);
        metrics.OptimalSampleOffsetSeconds.ShouldBeInRange(0, BitPeriod);
    }

    [Fact]
    public void CleanEye_HasNegligibleJitter()
    {
        // Ideal NRZ transitions cross the threshold at identical interpolated
        // instants each bit, so the RMS jitter must be far below one sample.
        var trace = NoisyNrzTrace(amplitude: 1.0, noiseSigma: 0.001);

        var metrics = Estimate(trace, threshold: 0.5);

        metrics.RmsJitterSeconds.ShouldBeLessThan(1.0 / SampleRate);
    }

    [Fact]
    public void EmptyLevel_YieldsClosedEyeInsteadOfCrashing()
    {
        // All-zeros trace with noise: no marks above threshold anywhere.
        var trace = NoisyNrzTrace(amplitude: 0.0, noiseSigma: 0.001);

        var metrics = Estimate(trace, threshold: 0.5);

        metrics.QFactor.ShouldBe(0);
        metrics.BerEstimate.ShouldBe(0.5);
    }
}
