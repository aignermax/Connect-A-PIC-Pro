using CAP_Core.LightCalculation.TimeDomainSimulation.PhaseNoise;
using Shouldly;
using Xunit;

namespace UnitTests.LightCalculation.TimeDomainSimulation.PhaseNoise;

/// <summary>
/// Issue #834: lock-group sharing, seeding and the nm↔Hz linewidth conversion
/// of the per-run phase-noise configuration.
/// </summary>
public class PhaseNoiseSettingsTests
{
    private const double LinewidthHz = 5e9;
    private const double DtSeconds = 1e-12;
    private const int NSamples = 64;

    [Fact]
    public void HasAnyNoise_AllZeroLinewidths_IsFalse()
    {
        var settings = new PhaseNoiseSettings();
        settings.AddSource(Guid.NewGuid(), new PhaseNoiseSource(0, "a"));
        settings.HasAnyNoise.ShouldBeFalse();
    }

    [Fact]
    public void HasAnyNoise_FiniteLinewidth_IsTrue()
    {
        var settings = new PhaseNoiseSettings();
        settings.AddSource(Guid.NewGuid(), new PhaseNoiseSource(LinewidthHz, "a"));
        settings.HasAnyNoise.ShouldBeTrue();
    }

    [Fact]
    public void BuildPhaseFactors_SameLockGroup_SharesOneWalk()
    {
        var pinA = Guid.NewGuid();
        var pinB = Guid.NewGuid();
        var settings = new PhaseNoiseSettings();
        settings.AddSource(pinA, new PhaseNoiseSource(LinewidthHz, "laser1"));
        settings.AddSource(pinB, new PhaseNoiseSource(LinewidthHz, "laser1"));

        var factors = settings.BuildPhaseFactors(DtSeconds, NSamples);
        factors[pinA].ShouldBe(factors[pinB]);
    }

    [Fact]
    public void BuildPhaseFactors_LockedWithOffset_KeepsConstantRelativePhase()
    {
        var pinA = Guid.NewGuid();
        var pinB = Guid.NewGuid();
        const double offset = 0.75;
        var settings = new PhaseNoiseSettings();
        settings.AddSource(pinA, new PhaseNoiseSource(LinewidthHz, "laser1"));
        settings.AddSource(pinB, new PhaseNoiseSource(LinewidthHz, "laser1", offset));

        var factors = settings.BuildPhaseFactors(DtSeconds, NSamples);
        for (int n = 0; n < NSamples; n++)
        {
            var relative = factors[pinB][n] * System.Numerics.Complex.Conjugate(factors[pinA][n]);
            relative.Phase.ShouldBe(offset, 1e-9);
        }
    }

    [Fact]
    public void BuildPhaseFactors_DifferentLockGroups_AreIndependent()
    {
        var pinA = Guid.NewGuid();
        var pinB = Guid.NewGuid();
        var settings = new PhaseNoiseSettings();
        settings.AddSource(pinA, new PhaseNoiseSource(LinewidthHz, "laser1"));
        settings.AddSource(pinB, new PhaseNoiseSource(LinewidthHz, "laser2"));

        var factors = settings.BuildPhaseFactors(DtSeconds, NSamples);
        factors[pinA].ShouldNotBe(factors[pinB]);
    }

    [Fact]
    public void BuildPhaseFactors_AllFactorsHaveUnitMagnitude()
    {
        var pin = Guid.NewGuid();
        var settings = new PhaseNoiseSettings();
        settings.AddSource(pin, new PhaseNoiseSource(LinewidthHz, "laser1"));

        var factors = settings.BuildPhaseFactors(DtSeconds, NSamples);
        foreach (var factor in factors[pin])
            factor.Magnitude.ShouldBe(1.0, 1e-12);
    }

    [Fact]
    public void BuildPhaseFactors_SameSeed_IsReproducible()
    {
        var pin = Guid.NewGuid();
        var a = new PhaseNoiseSettings(seed: 7);
        var b = new PhaseNoiseSettings(seed: 7);
        a.AddSource(pin, new PhaseNoiseSource(LinewidthHz, "laser1"));
        b.AddSource(pin, new PhaseNoiseSource(LinewidthHz, "laser1"));

        a.BuildPhaseFactors(DtSeconds, NSamples)[pin]
            .ShouldBe(b.BuildPhaseFactors(DtSeconds, NSamples)[pin]);
    }

    [Fact]
    public void LinewidthFwhmNmToHz_MatchesCDeltaLambdaOverLambdaSquared()
    {
        // Δν = c·Δλ/λ²: 0.1 nm at 1550 nm ≈ 12.48 GHz.
        double hz = PhaseNoiseSettings.LinewidthFwhmNmToHz(0.1, 1550);
        hz.ShouldBe(1.248e10, 1.248e10 * 0.01);
    }

    [Theory]
    [InlineData(0, 1550)]
    [InlineData(-1, 1550)]
    [InlineData(1, 0)]
    public void LinewidthFwhmNmToHz_InvalidInputs_ReturnZero(double fwhmNm, double centerNm)
    {
        PhaseNoiseSettings.LinewidthFwhmNmToHz(fwhmNm, centerNm).ShouldBe(0);
    }
}
