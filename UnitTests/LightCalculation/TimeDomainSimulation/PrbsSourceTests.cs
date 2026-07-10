using CAP_Core.LightCalculation.TimeDomainSimulation;
using CAP_Core.LightCalculation.TimeDomainSimulation.Sources;
using MathNet.Numerics.IntegralTransforms;
using Shouldly;
using System.Numerics;
using Xunit;

namespace UnitTests.LightCalculation.TimeDomainSimulation;

public class PrbsSourceTests
{
    private const double BitrateHz = 25e9;
    private const int SamplesPerSymbol = 32;

    private static TimeSignalDefinition CreateGrid(int symbolCount)
        => new(BitrateHz * SamplesPerSymbol, SamplesPerSymbol * symbolCount);

    [Fact]
    public void BitGenerator_Order7_HasPeriod127()
    {
        var bits = PrbsBitGenerator.Generate(order: 7, seed: 1, count: 254);

        for (int i = 0; i < 127; i++)
            bits[i].ShouldBe(bits[i + 127], $"bit {i} must repeat after one period");
    }

    [Fact]
    public void BitGenerator_Order7_OnePeriodHas64Marks()
    {
        var bits = PrbsBitGenerator.Generate(order: 7, seed: 1, count: 127);

        bits.Count(b => b).ShouldBe(64);
        bits.Count(b => !b).ShouldBe(63);
    }

    [Fact]
    public void BitGenerator_SameSeed_IsDeterministic()
    {
        var first = PrbsBitGenerator.Generate(order: 15, seed: 42, count: 500);
        var second = PrbsBitGenerator.Generate(order: 15, seed: 42, count: 500);

        first.ShouldBe(second);
    }

    [Fact]
    public void BitGenerator_DifferentSeeds_ProduceDifferentSequences()
    {
        var first = PrbsBitGenerator.Generate(order: 15, seed: 1, count: 200);
        var second = PrbsBitGenerator.Generate(order: 15, seed: 2, count: 200);

        first.SequenceEqual(second).ShouldBeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(32)]
    public void BitGenerator_UnsupportedOrder_Throws(int order)
    {
        Should.Throw<ArgumentException>(() => PrbsBitGenerator.Generate(order, seed: 1, count: 10));
    }

    [Fact]
    public void Generate_LengthMatchesGrid()
    {
        var grid = CreateGrid(symbolCount: 8);
        var source = new PrbsSource(BitrateHz, prbsOrder: 7);

        var samples = source.Generate(grid);

        samples.Length.ShouldBe(grid.NSamples);
    }

    [Fact]
    public void Generate_DomainIsOptical()
    {
        new PrbsSource(BitrateHz, prbsOrder: 7).Domain.ShouldBe(SignalDomain.Optical);
    }

    [Fact]
    public void Generate_BitCentresHonourExtinctionRatio()
    {
        // 10 dB extinction ratio is a POWER ratio: low amplitude = high / √10.
        var grid = CreateGrid(symbolCount: 127);
        var source = new PrbsSource(
            BitrateHz, prbsOrder: 7, highLevel: 1.0, extinctionRatioDb: 10.0, seed: 1);
        var bits = PrbsBitGenerator.Generate(order: 7, seed: 1, count: 127);
        double expectedLow = 1.0 / Math.Sqrt(10.0);

        var samples = source.Generate(grid);

        for (int bit = 0; bit < 127; bit++)
        {
            double centre = samples[bit * SamplesPerSymbol + SamplesPerSymbol / 2];
            double expected = bits[bit] ? 1.0 : expectedLow;
            centre.ShouldBe(expected, 1e-6, $"bit {bit} centre level");
        }
    }

    [Fact]
    public void Generate_SameSeed_IsDeterministic()
    {
        var grid = CreateGrid(symbolCount: 16);
        var first = new PrbsSource(BitrateHz, prbsOrder: 7, seed: 5).Generate(grid);
        var second = new PrbsSource(BitrateHz, prbsOrder: 7, seed: 5).Generate(grid);

        first.ShouldBe(second);
    }

    [Fact]
    public void Generate_RaisedCosineShaping_ReducesEnergyNearNyquist()
    {
        var grid = CreateGrid(symbolCount: 127);
        var shaped = new PrbsSource(BitrateHz, prbsOrder: 7, seed: 1).Generate(grid);
        var unshaped = new PrbsSource(BitrateHz, prbsOrder: 7, seed: 1, riseTimeFraction: 0.0)
            .Generate(grid);

        double shapedHigh = HighBandEnergyFraction(shaped);
        double unshapedHigh = HighBandEnergyFraction(unshaped);

        shapedHigh.ShouldBeLessThan(unshapedHigh,
            "raised-cosine edges must suppress spectral energy near Nyquist");
        shapedHigh.ShouldBeLessThan(1e-3,
            "band-limited PRBS must have negligible energy above 0.4 × sample rate");
    }

    [Fact]
    public void Constructor_InvalidExtinctionRatio_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(
            () => new PrbsSource(BitrateHz, prbsOrder: 7, extinctionRatioDb: -3.0));
    }

    [Fact]
    public void Constructor_NonPositiveBitrate_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(
            () => new PrbsSource(0, prbsOrder: 7));
    }

    /// <summary>Fraction of total spectral energy above 0.4 × sample rate (near Nyquist).</summary>
    private static double HighBandEnergyFraction(double[] samples)
    {
        var spectrum = samples.Select(s => new Complex(s, 0)).ToArray();
        Fourier.Forward(spectrum, FourierOptions.Matlab);

        int n = spectrum.Length;
        double total = 0, high = 0;
        for (int i = 1; i < n / 2; i++)
        {
            double energy = spectrum[i].Real * spectrum[i].Real
                + spectrum[i].Imaginary * spectrum[i].Imaginary;
            total += energy;
            if (i >= (int)(0.4 * n)) high += energy;
        }
        return high / total;
    }
}
