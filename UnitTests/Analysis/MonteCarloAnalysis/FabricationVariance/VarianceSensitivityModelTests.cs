using CAP_Core.Analysis.MonteCarloAnalysis.FabricationVariance;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.MonteCarloAnalysis.FabricationVariance
{
    public class VarianceSensitivityModelTests
    {
        private const double WavelengthNm = 1550;
        private const double LengthUm = 250;

        [Fact]
        public void Compute_ZeroDeviation_YieldsIdentityPerturbation()
        {
            var perturbation = VarianceSensitivityModel.Compute(
                ComponentVarianceKind.Straight, new ComponentDeviation(0, 0), WavelengthNm, LengthUm);

            perturbation.ShouldBe(SMatrixPerturbation.None);
        }

        [Theory]
        [InlineData(ComponentVarianceKind.Straight)]
        [InlineData(ComponentVarianceKind.Bend)]
        [InlineData(ComponentVarianceKind.Mmi)]
        [InlineData(ComponentVarianceKind.Coupler)]
        [InlineData(ComponentVarianceKind.Generic)]
        public void Compute_NonZeroDeviation_AmplitudeStaysPassive(ComponentVarianceKind kind)
        {
            var perturbation = VarianceSensitivityModel.Compute(
                kind, new ComponentDeviation(10, -5), WavelengthNm, LengthUm);

            perturbation.AmplitudeFactor.ShouldBeLessThan(1.0);
            perturbation.AmplitudeFactor.ShouldBeGreaterThan(0.0);
        }

        [Fact]
        public void Compute_BendLosesMoreThanStraight()
        {
            var deviation = new ComponentDeviation(10, 0);

            var straight = VarianceSensitivityModel.Compute(
                ComponentVarianceKind.Straight, deviation, WavelengthNm, LengthUm);
            var bend = VarianceSensitivityModel.Compute(
                ComponentVarianceKind.Bend, deviation, WavelengthNm, LengthUm);

            bend.AmplitudeFactor.ShouldBeLessThan(straight.AmplitudeFactor);
        }

        [Fact]
        public void Compute_PhaseScalesWithLength()
        {
            var deviation = new ComponentDeviation(5, 0);

            var oneTile = VarianceSensitivityModel.Compute(
                ComponentVarianceKind.Straight, deviation, WavelengthNm, LengthUm);
            var twoTiles = VarianceSensitivityModel.Compute(
                ComponentVarianceKind.Straight, deviation, WavelengthNm, 2 * LengthUm);

            twoTiles.PhaseRadians.ShouldBe(2 * oneTile.PhaseRadians, 1e-12);
        }

        [Fact]
        public void Compute_PhaseFollowsDeltaNEffSign()
        {
            var widened = VarianceSensitivityModel.Compute(
                ComponentVarianceKind.Straight, new ComponentDeviation(5, 0), WavelengthNm, LengthUm);
            var narrowed = VarianceSensitivityModel.Compute(
                ComponentVarianceKind.Straight, new ComponentDeviation(-5, 0), WavelengthNm, LengthUm);

            widened.PhaseRadians.ShouldBeGreaterThan(0);
            narrowed.PhaseRadians.ShouldBe(-widened.PhaseRadians, 1e-12);
        }

        [Fact]
        public void Compute_OnlyMmiGetsImbalance()
        {
            var deviation = new ComponentDeviation(10, 0);

            var mmi = VarianceSensitivityModel.Compute(
                ComponentVarianceKind.Mmi, deviation, WavelengthNm, LengthUm);
            var straight = VarianceSensitivityModel.Compute(
                ComponentVarianceKind.Straight, deviation, WavelengthNm, LengthUm);

            mmi.ImbalanceFraction.ShouldBe(
                VarianceSensitivityModel.MmiImbalancePerNmWidth * 10, 1e-12);
            straight.ImbalanceFraction.ShouldBe(0.0);
        }

        [Fact]
        public void Compute_OnlyCouplerGetsWavelengthShift()
        {
            var deviation = new ComponentDeviation(2, 3);

            var coupler = VarianceSensitivityModel.Compute(
                ComponentVarianceKind.Coupler, deviation, WavelengthNm, LengthUm);
            var bend = VarianceSensitivityModel.Compute(
                ComponentVarianceKind.Bend, deviation, WavelengthNm, LengthUm);

            double expectedShift = VarianceSensitivityModel.CouplerShiftNmPerNmWidth * 2
                + VarianceSensitivityModel.CouplerShiftNmPerNmThickness * 3;
            coupler.WavelengthShiftNm.ShouldBe(expectedShift, 1e-12);
            bend.WavelengthShiftNm.ShouldBe(0.0);
        }

        [Fact]
        public void Compute_MmiPhaseErrorIsAmplified()
        {
            var deviation = new ComponentDeviation(5, 0);

            var straight = VarianceSensitivityModel.Compute(
                ComponentVarianceKind.Straight, deviation, WavelengthNm, LengthUm);
            var mmi = VarianceSensitivityModel.Compute(
                ComponentVarianceKind.Mmi, deviation, WavelengthNm, LengthUm);

            mmi.PhaseRadians.ShouldBe(
                VarianceSensitivityModel.MmiPhaseErrorFactor * straight.PhaseRadians, 1e-12);
        }
    }
}
