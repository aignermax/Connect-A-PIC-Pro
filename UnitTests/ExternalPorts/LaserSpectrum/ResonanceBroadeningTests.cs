using CAP_Core.ExternalPorts.LaserSpectrum;
using Shouldly;
using Xunit;

namespace UnitTests.ExternalPorts.LaserSpectrum;

/// <summary>
/// Acceptance test for Issue #819: a laser with a finite linewidth washes out a
/// narrow resonance. The resonance is modelled analytically as a Lorentzian notch
/// filter; the transmitted power of a spectral source is the weight-averaged
/// transmission over its samples (incoherent superposition per wavelength).
/// </summary>
public class ResonanceBroadeningTests
{
    private const int ResonanceCenterNm = 1550;
    private const double ResonanceFwhmNm = 2.0;

    /// <summary>Lorentzian notch: full extinction at the resonance center.</summary>
    private static double NotchTransmission(int wavelengthNm)
    {
        double detuning = (wavelengthNm - ResonanceCenterNm) / (ResonanceFwhmNm / 2);
        return 1.0 - 1.0 / (1.0 + detuning * detuning);
    }

    private static double TransmittedPower(LaserSpectrumModel spectrum) =>
        spectrum.GetSamples().Sum(s => s.Weight * NotchTransmission(s.WavelengthNm));

    [Fact]
    public void IdealSource_SeesFullResonanceContrast()
    {
        var ideal = new LaserSpectrumModel(ResonanceCenterNm);

        TransmittedPower(ideal).ShouldBe(0.0, tolerance: 1e-12);
    }

    [Theory]
    [InlineData(LaserLineShape.Gaussian)]
    [InlineData(LaserLineShape.Lorentzian)]
    public void FiniteLinewidth_ReducesResonanceContrast(LaserLineShape shape)
    {
        var broadened = new LaserSpectrumModel(ResonanceCenterNm, shape, fwhmNm: 4);

        double residual = TransmittedPower(broadened);

        // The notch no longer extinguishes the source: a measurable share of the
        // power leaks through at the side wavelengths.
        residual.ShouldBeGreaterThan(0.2);
        residual.ShouldBeLessThan(1.0);
    }

    [Fact]
    public void WiderLinewidth_LosesMoreContrast()
    {
        double narrow = TransmittedPower(
            new LaserSpectrumModel(ResonanceCenterNm, LaserLineShape.Gaussian, 2));
        double wide = TransmittedPower(
            new LaserSpectrumModel(ResonanceCenterNm, LaserLineShape.Gaussian, 8));

        wide.ShouldBeGreaterThan(narrow);
    }
}
