using CAP_Core.ExternalPorts.LaserSpectrum;
using Shouldly;
using Xunit;

namespace UnitTests.ExternalPorts.LaserSpectrum;

public class LaserSpectrumModelTests
{
    private const int Center = 1550;

    [Fact]
    public void IdealSource_YieldsSingleFullWeightSample()
    {
        var spectrum = new LaserSpectrumModel(Center);

        var samples = spectrum.GetSamples();

        samples.Count.ShouldBe(1);
        samples[0].WavelengthNm.ShouldBe(Center);
        samples[0].Weight.ShouldBe(1.0);
    }

    [Theory]
    [InlineData(LaserLineShape.Gaussian, 0)]
    [InlineData(LaserLineShape.Lorentzian, -3)]
    public void NonPositiveFwhm_FallsBackToIdeal(LaserLineShape shape, double fwhm)
    {
        var spectrum = new LaserSpectrumModel(Center, shape, fwhm);

        spectrum.IsIdeal.ShouldBeTrue();
        spectrum.GetSamples().Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(LaserLineShape.Gaussian)]
    [InlineData(LaserLineShape.Lorentzian)]
    public void FiniteLinewidth_SamplesAreNormalizedAndCenteredOnPeak(LaserLineShape shape)
    {
        var spectrum = new LaserSpectrumModel(Center, shape, fwhmNm: 4);

        var samples = spectrum.GetSamples();

        samples.Count.ShouldBeGreaterThan(1);
        samples.Sum(s => s.Weight).ShouldBe(1.0, tolerance: 1e-12);
        var peak = samples.MaxBy(s => s.Weight);
        peak.WavelengthNm.ShouldBe(Center);
    }

    [Fact]
    public void GaussianSamples_AreSymmetricAroundCenter()
    {
        var samples = new LaserSpectrumModel(Center, LaserLineShape.Gaussian, 4).GetSamples();

        foreach (var sample in samples)
        {
            int mirror = 2 * Center - sample.WavelengthNm;
            var mirrored = samples.Single(s => s.WavelengthNm == mirror);
            mirrored.Weight.ShouldBe(sample.Weight, tolerance: 1e-12);
        }
    }

    [Fact]
    public void WeightAtHalfFwhm_IsHalfThePeakWeight()
    {
        const double fwhm = 6;
        var samples = new LaserSpectrumModel(Center, LaserLineShape.Gaussian, fwhm).GetSamples();

        double peak = samples.Single(s => s.WavelengthNm == Center).Weight;
        double atHalfWidth = samples.Single(s => s.WavelengthNm == Center + (int)(fwhm / 2)).Weight;

        atHalfWidth.ShouldBe(peak / 2, tolerance: peak * 0.01);
    }

    [Fact]
    public void LorentzianTails_AreHeavierThanGaussian()
    {
        const double fwhm = 4;
        var gauss = new LaserSpectrumModel(Center, LaserLineShape.Gaussian, fwhm).GetSamples();
        var lorentz = new LaserSpectrumModel(Center, LaserLineShape.Lorentzian, fwhm).GetSamples();

        int tailWavelength = Center + (int)(2 * fwhm);
        double gaussTail = gauss.FirstOrDefault(s => s.WavelengthNm == tailWavelength).Weight;
        double lorentzTail = lorentz.Single(s => s.WavelengthNm == tailWavelength).Weight;

        lorentzTail.ShouldBeGreaterThan(gaussTail);
    }

    [Fact]
    public void HugeFwhm_IsCappedAtMaxWindow()
    {
        var samples = new LaserSpectrumModel(Center, LaserLineShape.Lorentzian, 1000).GetSamples();

        samples.Count.ShouldBe(2 * LaserSpectrumModel.MaxHalfWindowNm + 1);
    }

    [Fact]
    public void SamplesNearZeroWavelength_AreClampedToPositiveValues()
    {
        var samples = new LaserSpectrumModel(3, LaserLineShape.Lorentzian, 2).GetSamples();

        samples.ShouldAllBe(s => s.WavelengthNm > 0);
        samples.Sum(s => s.Weight).ShouldBe(1.0, tolerance: 1e-12);
    }

    [Fact]
    public void InvalidCenterWavelength_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new LaserSpectrumModel(0));
    }
}
