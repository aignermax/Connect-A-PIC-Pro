using CAP_Core.LightCalculation.TimeDomainSimulation.CompactModels;
using CAP_Core.LightCalculation.TimeDomainSimulation.CompactModels.Models;
using Shouldly;
using Xunit;

namespace UnitTests.LightCalculation.TimeDomainSimulation.CompactModels;

public class LaserDiodeRateEquationModelTests
{
    private const double Dt = 1e-12;   // 1 ps timestep
    private const int NSamples = 4096; // ≈ 4 ns run — long enough to settle

    private static double[] CurrentStep(double amps, int nSamples)
        => Enumerable.Repeat(amps, nSamples).ToArray();

    private static double MeanOfLastTenPercent(double[] trace)
    {
        int start = trace.Length * 9 / 10;
        return trace.Skip(start).Average();
    }

    [Fact]
    public void ThresholdCurrent_DefaultParameters_IsPhysicallyPlausible()
    {
        var model = new LaserDiodeRateEquationModel();
        // Typical DFB threshold is a few mA to a few tens of mA.
        model.ThresholdCurrentAmps.ShouldBeGreaterThan(1e-3);
        model.ThresholdCurrentAmps.ShouldBeLessThan(100e-3);
    }

    [Fact]
    public void Step_CurrentStepAboveThreshold_ShowsRelaxationOscillationThenSettles()
    {
        var model = new LaserDiodeRateEquationModel();
        double driveAmps = 2.0 * model.ThresholdCurrentAmps;

        var result = ActiveComponentStepper.StepOverTrace(
            model, Dt, NSamples, electricalInput: CurrentStep(driveAmps, NSamples));

        var power = result.ElectricalOutput;
        double steadyState = MeanOfLastTenPercent(power);
        double peak = power.Max();

        // Canonical turn-on: overshoot well above the steady state …
        steadyState.ShouldBeGreaterThan(0);
        peak.ShouldBeGreaterThan(steadyState * 1.3,
            $"Expected relaxation-oscillation overshoot; peak={peak:E3}, steady={steadyState:E3}");

        // … the peak happens during turn-on, not at the end …
        int peakIndex = Array.IndexOf(power, peak);
        peakIndex.ShouldBeLessThan(NSamples / 2);

        // … and the trace settles (last 10% varies < 2%).
        int start = NSamples * 9 / 10;
        double maxDeviation = power.Skip(start).Max(p => Math.Abs(p - steadyState));
        maxDeviation.ShouldBeLessThan(steadyState * 0.02);
    }

    [Fact]
    public void Step_CurrentBelowThreshold_EmitsOnlySpontaneousBackground()
    {
        var model = new LaserDiodeRateEquationModel();
        double belowThreshold = 0.5 * model.ThresholdCurrentAmps;
        double aboveThreshold = 2.0 * model.ThresholdCurrentAmps;

        var below = ActiveComponentStepper.StepOverTrace(
            model, Dt, NSamples, electricalInput: CurrentStep(belowThreshold, NSamples));
        var above = ActiveComponentStepper.StepOverTrace(
            model, Dt, NSamples, electricalInput: CurrentStep(aboveThreshold, NSamples));

        double powerBelow = MeanOfLastTenPercent(below.ElectricalOutput);
        double powerAbove = MeanOfLastTenPercent(above.ElectricalOutput);

        // Below threshold only β-spontaneous emission leaks out — orders of magnitude less.
        powerBelow.ShouldBeLessThan(powerAbove * 0.01);
    }

    [Fact]
    public void Step_OutgoingField_IsSquareRootOfPower()
    {
        var model = new LaserDiodeRateEquationModel();
        double driveAmps = 2.0 * model.ThresholdCurrentAmps;

        var result = ActiveComponentStepper.StepOverTrace(
            model, Dt, NSamples, electricalInput: CurrentStep(driveAmps, NSamples));

        int last = NSamples - 1;
        double fieldSquared = result.OutgoingField[last].Magnitude * result.OutgoingField[last].Magnitude;
        fieldSquared.ShouldBe(result.ElectricalOutput[last], result.ElectricalOutput[last] * 1e-9);
    }

    [Fact]
    public void Step_ProducesNoNaNOrInfinity()
    {
        var model = new LaserDiodeRateEquationModel();
        double driveAmps = 3.0 * model.ThresholdCurrentAmps;

        var result = ActiveComponentStepper.StepOverTrace(
            model, Dt, NSamples, electricalInput: CurrentStep(driveAmps, NSamples));

        result.ElectricalOutput.ShouldAllBe(p => double.IsFinite(p) && p >= 0);
    }

    [Fact]
    public void Step_PathologicalTimestep_ThrowsInsteadOfHanging()
    {
        var model = new LaserDiodeRateEquationModel();
        var state = model.CreateInitialState();
        // dt of 1 s vs τp = 3 ps would need >> 100 000 substeps.
        Should.Throw<InvalidOperationException>(() => model.Step(1.0, default, state, 0.01));
    }
}
