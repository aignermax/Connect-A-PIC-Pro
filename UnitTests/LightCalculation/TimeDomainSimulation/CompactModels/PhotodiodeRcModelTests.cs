using CAP_Core.LightCalculation.TimeDomainSimulation.CompactModels;
using CAP_Core.LightCalculation.TimeDomainSimulation.CompactModels.Models;
using Shouldly;
using System.Numerics;
using Xunit;

namespace UnitTests.LightCalculation.TimeDomainSimulation.CompactModels;

public class PhotodiodeRcModelTests
{
    private const double Dt = 1e-12;                    // 1 ps timestep
    private const double Responsivity = 0.8;            // A/W
    private const double Tau = 100e-12;                 // 100 ps RC constant
    private const double InputPowerWatts = 1e-3;        // 1 mW

    private static PhotodiodeRcModel CreateModel() => new(new Dictionary<string, double>
    {
        [PhotodiodeRcModel.ResponsivityKey] = Responsivity,
        [PhotodiodeRcModel.TimeConstantKey] = Tau,
    });

    [Fact]
    public void Step_ConstantPower_SettlesAtDcResponsivity()
    {
        var model = CreateModel();
        int nSamples = 2000; // 2 ns >> τ → fully settled
        var incident = Enumerable.Repeat(new Complex(Math.Sqrt(InputPowerWatts), 0), nSamples).ToArray();

        var result = ActiveComponentStepper.StepOverTrace(model, Dt, nSamples, incidentField: incident);

        double expectedDc = Responsivity * InputPowerWatts;
        result.ElectricalOutput[^1].ShouldBe(expectedDc, expectedDc * 0.001);
    }

    [Fact]
    public void Step_PowerStep_ShowsRcRollOff()
    {
        // At t = τ the photocurrent of a first-order RC must reach 1 - 1/e ≈ 63.2%.
        var model = CreateModel();
        int nSamples = 500;
        var incident = Enumerable.Repeat(new Complex(Math.Sqrt(InputPowerWatts), 0), nSamples).ToArray();

        var result = ActiveComponentStepper.StepOverTrace(model, Dt, nSamples, incidentField: incident);

        int sampleAtTau = (int)(Tau / Dt) - 1; // current after (n+1)·dt = τ
        double expected = Responsivity * InputPowerWatts * (1.0 - Math.Exp(-1.0));
        result.ElectricalOutput[sampleAtTau].ShouldBe(expected, expected * 0.02);

        // Roll-off: the response must lag the instantaneous target.
        result.ElectricalOutput[0].ShouldBeLessThan(Responsivity * InputPowerWatts * 0.05);
    }

    [Fact]
    public void Step_AbsorbsIncidentLight()
    {
        var model = CreateModel();
        var state = model.CreateInitialState();

        var step = model.Step(Dt, new Complex(1.0, 0.5), state, 0.0);

        step.OutgoingField.ShouldBe(Complex.Zero);
    }

    [Fact]
    public void Step_ComplexField_UsesMagnitudeSquaredAsPower()
    {
        var model = CreateModel();
        var stateReal = model.CreateInitialState();
        var stateComplex = model.CreateInitialState();

        // Same power, different phase → identical photocurrent.
        var real = model.Step(Dt, new Complex(0.3, 0), stateReal, 0.0);
        var rotated = model.Step(Dt, Complex.FromPolarCoordinates(0.3, 1.2), stateComplex, 0.0);

        rotated.ElectricalOutput.ShouldBe(real.ElectricalOutput, 1e-15);
    }

    [Fact]
    public void Constructor_InvalidParameters_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new PhotodiodeRcModel(
            new Dictionary<string, double> { [PhotodiodeRcModel.ResponsivityKey] = -1 }));
        Should.Throw<ArgumentOutOfRangeException>(() => new PhotodiodeRcModel(
            new Dictionary<string, double> { [PhotodiodeRcModel.TimeConstantKey] = 0 }));
    }
}
