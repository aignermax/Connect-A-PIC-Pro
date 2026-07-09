using CAP_Core.LightCalculation.TimeDomainSimulation.CompactModels;
using CAP_Core.LightCalculation.TimeDomainSimulation.CompactModels.Models;
using Shouldly;
using System.Numerics;
using Xunit;

namespace UnitTests.LightCalculation.TimeDomainSimulation.CompactModels;

public class ElectroOpticPhaseModulatorModelTests
{
    private const double Dt = 1e-12;
    private const double VPi = 4.0;

    private static ElectroOpticPhaseModulatorModel CreateModel(double lossDb = 0.0)
        => new(new Dictionary<string, double>
        {
            [ElectroOpticPhaseModulatorModel.VPiKey] = VPi,
            [ElectroOpticPhaseModulatorModel.InsertionLossKey] = lossDb,
        });

    [Fact]
    public void Step_VoltageRamp_ProducesLinearPhase()
    {
        var model = CreateModel();
        int nSamples = 100;
        double voltsPerSample = 0.05;
        var ramp = Enumerable.Range(0, nSamples).Select(n => n * voltsPerSample).ToArray();
        var incident = Enumerable.Repeat(Complex.One, nSamples).ToArray();

        var result = ActiveComponentStepper.StepOverTrace(
            model, Dt, nSamples, incidentField: incident, electricalInput: ramp);

        // Analytic: φ[n] = π · V[n] / V_π, exactly linear in n.
        for (int n = 0; n < nSamples; n++)
        {
            double expected = Math.PI * ramp[n] / VPi;
            result.ElectricalOutput[n].ShouldBe(expected, 1e-12);
            result.OutgoingField[n].Phase.ShouldBe(WrapPhase(expected), 1e-12);
        }
    }

    [Fact]
    public void Step_HalfWaveVoltage_GivesPiPhaseShift()
    {
        var model = CreateModel();
        var state = model.CreateInitialState();

        var step = model.Step(Dt, Complex.One, state, VPi);

        step.ElectricalOutput.ShouldBe(Math.PI, 1e-12);
        // exp(iπ) = −1
        step.OutgoingField.Real.ShouldBe(-1.0, 1e-12);
        step.OutgoingField.Imaginary.ShouldBe(0.0, 1e-12);
    }

    [Fact]
    public void Step_NoLoss_PreservesFieldMagnitude()
    {
        var model = CreateModel();
        var state = model.CreateInitialState();
        var incident = new Complex(0.6, 0.3);

        var step = model.Step(Dt, incident, state, 1.7);

        step.OutgoingField.Magnitude.ShouldBe(incident.Magnitude, 1e-12);
    }

    [Fact]
    public void Step_WithInsertionLoss_AttenuatesAmplitude()
    {
        const double lossDb = 3.0;
        var model = CreateModel(lossDb);
        var state = model.CreateInitialState();

        var step = model.Step(Dt, Complex.One, state, 0.0);

        double expectedAmplitude = Math.Pow(10.0, -lossDb / 20.0);
        step.OutgoingField.Magnitude.ShouldBe(expectedAmplitude, 1e-12);
    }

    [Fact]
    public void Constructor_InvalidParameters_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new ElectroOpticPhaseModulatorModel(
            new Dictionary<string, double> { [ElectroOpticPhaseModulatorModel.VPiKey] = 0 }));
        Should.Throw<ArgumentOutOfRangeException>(() => new ElectroOpticPhaseModulatorModel(
            new Dictionary<string, double> { [ElectroOpticPhaseModulatorModel.InsertionLossKey] = -1 }));
    }

    /// <summary>Wraps a phase to (−π, π] to match <see cref="Complex.Phase"/>.</summary>
    private static double WrapPhase(double phase)
    {
        double wrapped = Math.IEEERemainder(phase, 2 * Math.PI);
        return wrapped <= -Math.PI ? wrapped + 2 * Math.PI : wrapped;
    }
}
