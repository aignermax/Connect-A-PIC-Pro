using CAP_Core.Solvers.ModeProbe;
using Shouldly;
using Xunit;

namespace UnitTests.Solvers.ModeProbe;

public class FiberOverlapCalculatorTests
{
    [Fact]
    public void IdenticalGaussians_GiveUnityEfficiency()
    {
        var result = FiberOverlapCalculator.Compute(10.4, 10.4, 10.4);

        result.Efficiency.ShouldBe(1.0, 1e-12);
        result.EfficiencyPercent.ShouldBe(100.0, 1e-9);
        result.LossDb.ShouldBe(0.0, 1e-9);
    }

    [Fact]
    public void AnalyticCase_MatchesClosedForm()
    {
        // wx=0.6, wy=0.5, wf=5.2 (µm radii) → MFDs 1.2, 1.0, 10.4.
        // ηx = 2·0.6·5.2/(0.36+27.04), ηy = 2·0.5·5.2/(0.25+27.04)
        double etaX = 2 * 0.6 * 5.2 / (0.6 * 0.6 + 5.2 * 5.2);
        double etaY = 2 * 0.5 * 5.2 / (0.5 * 0.5 + 5.2 * 5.2);

        var result = FiberOverlapCalculator.Compute(1.2, 1.0, 10.4);

        result.Efficiency.ShouldBe(etaX * etaY, 1e-12);
    }

    [Fact]
    public void SmallModeIntoLargeFiber_HasHighLoss()
    {
        // A sub-µm waveguide mode into SMF-28 without a spot-size converter:
        // the classic elliptical-vs-round mismatch → efficiency must be low.
        var result = FiberOverlapCalculator.Compute(1.1, 0.9, 10.4);

        result.Efficiency.ShouldBeLessThan(0.1);
        result.LossDb.ShouldBeGreaterThan(10.0);
    }

    [Fact]
    public void EfficiencyIsSymmetricInAxes()
    {
        var a = FiberOverlapCalculator.Compute(2.0, 3.0, 10.4);
        var b = FiberOverlapCalculator.Compute(3.0, 2.0, 10.4);

        a.Efficiency.ShouldBe(b.Efficiency, 1e-12);
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, -2, 1)]
    [InlineData(1, 1, 0)]
    [InlineData(double.NaN, 1, 1)]
    public void NonPositiveInputs_Throw(double mfdX, double mfdY, double fiberMfd)
    {
        Should.Throw<ArgumentOutOfRangeException>(
            () => FiberOverlapCalculator.Compute(mfdX, mfdY, fiberMfd));
    }

    [Fact]
    public void ZeroEfficiencyRecord_ReportsInfiniteLoss()
    {
        new FiberOverlapResult(0).LossDb.ShouldBe(double.PositiveInfinity);
    }
}
