using CAP_Core.LightCalculation.TimeDomainSimulation.Sampling;
using Shouldly;
using Xunit;

namespace UnitTests.LightCalculation.TimeDomainSimulation;

public class SamplingPolicyTests
{
    private const double BitrateHz = 25e9;

    [Fact]
    public void CreateGrid_SampleRateIsBitrateTimesSamplesPerSymbol()
    {
        var grid = SamplingPolicy.CreateGrid(
            BitrateHz, samplesPerSymbol: 32, symbolCount: 8, guardSamples: 0);

        grid.SampleRateHz.ShouldBe(25e9 * 32);
    }

    [Fact]
    public void CreateGrid_NSamplesCoversSymbolsPlusGuard()
    {
        var grid = SamplingPolicy.CreateGrid(
            BitrateHz, samplesPerSymbol: 32, symbolCount: 8, guardSamples: 256);

        grid.NSamples.ShouldBe(32 * 8 + 256);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(1)]
    [InlineData(0)]
    public void CreateGrid_RejectsSamplesPerSymbolBelowMinimum(int samplesPerSymbol)
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            SamplingPolicy.CreateGrid(BitrateHz, samplesPerSymbol, symbolCount: 8, guardSamples: 0));
    }

    [Fact]
    public void CreateGrid_MinimumSamplesPerSymbolIsSixteen()
    {
        SamplingPolicy.MinSamplesPerSymbol.ShouldBe(16);

        var grid = SamplingPolicy.CreateGrid(
            BitrateHz, SamplingPolicy.MinSamplesPerSymbol, symbolCount: 4, guardSamples: 0);
        grid.NSamples.ShouldBe(16 * 4);
    }

    [Fact]
    public void CreateGrid_RejectsNonPositiveBitrate()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            SamplingPolicy.CreateGrid(0, samplesPerSymbol: 32, symbolCount: 8, guardSamples: 0));
    }

    [Fact]
    public void CreateGrid_RejectsNonPositiveSymbolCount()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            SamplingPolicy.CreateGrid(BitrateHz, samplesPerSymbol: 32, symbolCount: 0, guardSamples: 0));
    }

    [Fact]
    public void CreateGrid_RejectsNegativeGuard()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            SamplingPolicy.CreateGrid(BitrateHz, samplesPerSymbol: 32, symbolCount: 8, guardSamples: -1));
    }
}
