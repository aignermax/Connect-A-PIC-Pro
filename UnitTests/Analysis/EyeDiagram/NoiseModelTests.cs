using CAP_Core.Analysis.EyeDiagram;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.EyeDiagram;

public class NoiseModelTests
{
    private static NoiseModel CreateModel() => new()
    {
        ResponsivityAPerW = 0.8,
        BandwidthHz = 18.75e9,
        RinDbPerHz = -145,
        TemperatureKelvin = 300,
        LoadResistanceOhm = 50,
    };

    [Fact]
    public void ShotNoise_GrowsWithSquareRootOfPower()
    {
        var model = CreateModel();

        double sigma1 = model.ShotNoiseSigmaAmpere(1e-3);
        double sigma4 = model.ShotNoiseSigmaAmpere(4e-3);

        sigma4.ShouldBe(2 * sigma1, sigma1 * 1e-9);
    }

    [Fact]
    public void ShotNoise_MatchesTextbookFormula()
    {
        var model = CreateModel();

        // σ² = 2 q R P B with q = 1.602e-19, R = 0.8, P = 1 mW, B = 18.75 GHz
        double expected = Math.Sqrt(2 * 1.602176634e-19 * 0.8 * 1e-3 * 18.75e9);
        model.ShotNoiseSigmaAmpere(1e-3).ShouldBe(expected, expected * 1e-9);
    }

    [Fact]
    public void ThermalNoise_IsIndependentOfPower()
    {
        var model = CreateModel();

        double expected = Math.Sqrt(4 * 1.380649e-23 * 300 * 18.75e9 / 50);
        model.ThermalNoiseSigmaAmpere().ShouldBe(expected, expected * 1e-9);
    }

    [Fact]
    public void RinNoise_ScalesLinearlyWithPower()
    {
        var model = CreateModel();

        double sigma1 = model.RinNoiseSigmaAmpere(1e-3);
        double sigma2 = model.RinNoiseSigmaAmpere(2e-3);

        sigma2.ShouldBe(2 * sigma1, sigma1 * 1e-9);
    }

    [Fact]
    public void TotalSigma_AtZeroPower_ReducesToThermalNoise()
    {
        var model = CreateModel();

        double expected = model.ThermalNoiseSigmaAmpere() / model.ResponsivityAPerW;
        model.TotalSigmaOpticalPower(0).ShouldBe(expected, expected * 1e-9);
    }

    [Fact]
    public void TotalSigma_IncreasesMonotonicallyWithPower()
    {
        var model = CreateModel();

        double low = model.TotalSigmaOpticalPower(1e-4);
        double mid = model.TotalSigmaOpticalPower(1e-3);
        double high = model.TotalSigmaOpticalPower(1e-2);

        mid.ShouldBeGreaterThan(low);
        high.ShouldBeGreaterThan(mid);
    }

    [Fact]
    public void NegativePower_IsClampedToZero()
    {
        var model = CreateModel();

        model.ShotNoiseSigmaAmpere(-1).ShouldBe(0);
        model.RinNoiseSigmaAmpere(-1).ShouldBe(0);
    }
}
