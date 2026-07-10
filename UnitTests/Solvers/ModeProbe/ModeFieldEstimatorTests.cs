using CAP_Core.Solvers.ModeProbe;
using Shouldly;
using Xunit;

namespace UnitTests.Solvers.ModeProbe;

public class ModeFieldEstimatorTests
{
    [Fact]
    public void GuidedMode_MfdExceedsCoreSize()
    {
        var mfd = ModeFieldEstimator.EstimateMfd(0.45, 0.22, nEff: 2.4, cladIndex: 1.44, wavelengthMicrometers: 1.55);

        mfd.ShouldNotBeNull();
        mfd.Value.MfdX.ShouldBeGreaterThan(0.45);
        mfd.Value.MfdY.ShouldBeGreaterThan(0.22);
        // Both axes share the same penetration depth in this first-order estimate.
        (mfd.Value.MfdX - 0.45).ShouldBe(mfd.Value.MfdY - 0.22, 1e-12);
    }

    [Fact]
    public void PenetrationDepth_MatchesClosedForm()
    {
        const double nEff = 2.4, nClad = 1.44, lambda = 1.55;
        double delta = lambda / (2 * Math.PI * Math.Sqrt(nEff * nEff - nClad * nClad));

        var mfd = ModeFieldEstimator.EstimateMfd(0.5, 0.3, nEff, nClad, lambda);

        mfd!.Value.MfdX.ShouldBe(0.5 + 2 * delta, 1e-12);
        mfd.Value.MfdY.ShouldBe(0.3 + 2 * delta, 1e-12);
    }

    [Fact]
    public void WeakerConfinement_GivesLargerMfd()
    {
        var strong = ModeFieldEstimator.EstimateMfd(0.45, 0.22, 2.8, 1.44, 1.55);
        var weak = ModeFieldEstimator.EstimateMfd(0.45, 0.22, 1.6, 1.44, 1.55);

        weak!.Value.MfdX.ShouldBeGreaterThan(strong!.Value.MfdX);
    }

    [Theory]
    [InlineData(1.44)]  // n_eff == n_clad: cutoff
    [InlineData(1.20)]  // n_eff < n_clad: unphysical / unguided
    public void UnguidedMode_ReturnsNull(double nEff)
    {
        ModeFieldEstimator.EstimateMfd(0.45, 0.22, nEff, 1.44, 1.55).ShouldBeNull();
    }

    [Theory]
    [InlineData(0, 0.22, 1.55)]
    [InlineData(0.45, 0, 1.55)]
    [InlineData(0.45, 0.22, 0)]
    public void InvalidGeometry_ReturnsNull(double width, double height, double wavelength)
    {
        ModeFieldEstimator.EstimateMfd(width, height, 2.4, 1.44, wavelength).ShouldBeNull();
    }
}
