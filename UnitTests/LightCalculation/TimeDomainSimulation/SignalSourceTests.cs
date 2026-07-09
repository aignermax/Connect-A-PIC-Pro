using CAP_Core.LightCalculation.TimeDomainSimulation;
using CAP_Core.LightCalculation.TimeDomainSimulation.Sources;
using Shouldly;
using Xunit;

namespace UnitTests.LightCalculation.TimeDomainSimulation;

/// <summary>Tests for the simple signal sources (CW and Gaussian pulse).</summary>
public class SignalSourceTests
{
    private static readonly TimeSignalDefinition Grid = new(sampleRateHz: 1e12, nSamples: 64);

    [Fact]
    public void CwSource_GeneratesConstantAmplitude()
    {
        var source = new CwSource(amplitude: 0.75);

        var samples = source.Generate(Grid);

        samples.Length.ShouldBe(Grid.NSamples);
        samples.ShouldAllBe(s => s == 0.75);
    }

    [Fact]
    public void CwSource_DomainIsOptical()
    {
        new CwSource(1.0).Domain.ShouldBe(SignalDomain.Optical);
    }

    [Fact]
    public void CwSource_NegativeAmplitude_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new CwSource(-1.0));
    }

    [Fact]
    public void PulseSource_MatchesExistingGaussianFactory()
    {
        double centre = 20 * Grid.TimeStepSeconds;
        double sigma = 3 * Grid.TimeStepSeconds;
        var source = new PulseSource(centre, sigma, amplitude: 2.0);

        var samples = source.Generate(Grid);

        var expected = Grid.CreateGaussianPulse(centre, sigma, amplitude: 2.0);
        samples.ShouldBe(expected);
    }

    [Fact]
    public void PulseSource_DomainIsOptical()
    {
        new PulseSource(1e-12, 1e-13).Domain.ShouldBe(SignalDomain.Optical);
    }

    [Fact]
    public void PulseSource_NonPositiveSigma_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new PulseSource(1e-12, 0));
    }
}
