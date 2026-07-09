using CAP_Core.LightCalculation;
using CAP_Core.LightCalculation.TimeDomainSimulation;
using CAP_Core.LightCalculation.TimeDomainSimulation.Sampling;
using CAP_Core.LightCalculation.TimeDomainSimulation.Sources;
using Moq;
using Shouldly;
using System.Numerics;
using Xunit;

namespace UnitTests.LightCalculation.TimeDomainSimulation;

/// <summary>
/// The keystone guard of design #600 (§5.1): for purely passive circuits the
/// stepped <see cref="SampleModeTransientDriver"/> must reproduce the #527
/// batch convolution (<see cref="TimeDomainSimulator.Run"/>) within a tight
/// tolerance, proving the restructuring did not change the physics.
/// </summary>
public class SampleModeDriverEquivalenceTests
{
    private const double SpeedOfLightNmPerS = 2.998e17;
    private const double CenterWavelengthNm = 1550;
    private const double SpanNm = 100;
    private const int NPoints = 64;
    private const double EquivalenceTolerance = 1e-9;

    private static Mock<ISystemMatrixBuilder> CreateTwoPortBuilder(
        Guid inputPin, Guid outputPin, Func<int, Complex> s21ByWavelength)
    {
        var mockBuilder = new Mock<ISystemMatrixBuilder>();
        mockBuilder.Setup(b => b.GetSystemSMatrix(It.IsAny<int>()))
            .Returns((int wavelengthNm) =>
            {
                var matrix = new SMatrix(new List<Guid> { inputPin, outputPin }, new());
                matrix.SetValues(new Dictionary<(Guid, Guid), Complex>
                {
                    { (inputPin, outputPin), s21ByWavelength(wavelengthNm) }
                });
                return matrix;
            });
        return mockBuilder;
    }

    private static Mock<ISystemMatrixBuilder> CreateSplitterBuilder(
        Guid inputPin, Guid outputPin1, Guid outputPin2)
    {
        double coupling = 1.0 / Math.Sqrt(2.0);
        var mockBuilder = new Mock<ISystemMatrixBuilder>();
        mockBuilder.Setup(b => b.GetSystemSMatrix(It.IsAny<int>()))
            .Returns((int _) =>
            {
                var matrix = new SMatrix(
                    new List<Guid> { inputPin, outputPin1, outputPin2 }, new());
                matrix.SetValues(new Dictionary<(Guid, Guid), Complex>
                {
                    { (inputPin, outputPin1), coupling },
                    { (inputPin, outputPin2), coupling },
                });
                return matrix;
            });
        return mockBuilder;
    }

    private static void AssertTracesMatch(
        TimeDomainResult batch, TimeDomainResult stepped, Guid pin)
    {
        stepped.PinTraces.ShouldContainKey(pin);
        var batchTrace = batch.PinTraces[pin];
        var steppedTrace = stepped.PinTraces[pin];
        steppedTrace.Length.ShouldBe(batchTrace.Length);
        for (int i = 0; i < batchTrace.Length; i++)
            steppedTrace[i].ShouldBe(batchTrace[i], EquivalenceTolerance, $"sample {i}");
    }

    [Fact]
    public void StraightWaveguide_GaussianPulse_MatchesBatchSimulator()
    {
        var inputPin = Guid.NewGuid();
        var outputPin = Guid.NewGuid();
        var builder = CreateTwoPortBuilder(inputPin, outputPin, _ => Complex.One);

        var timeDef = TimeSignalDefinition.FromWavelengthSweep(CenterWavelengthNm, SpanNm, NPoints);
        var input = timeDef.CreateGaussianPulse(20 * timeDef.TimeStepSeconds, 3 * timeDef.TimeStepSeconds);
        var inputSignals = new Dictionary<Guid, double[]> { { inputPin, input } };

        var batch = new TimeDomainSimulator(builder.Object)
            .Run(inputSignals, timeDef, CenterWavelengthNm, SpanNm, NPoints);
        var stepped = new SampleModeTransientDriver(builder.Object)
            .Run(inputSignals, timeDef, null, CenterWavelengthNm, SpanNm, NPoints);

        AssertTracesMatch(batch, stepped, outputPin);
    }

    [Fact]
    public void GroupDelayedWaveguide_MatchesBatchSimulator()
    {
        var inputPin = Guid.NewGuid();
        var outputPin = Guid.NewGuid();

        double fMin = SpeedOfLightNmPerS / (CenterWavelengthNm + SpanNm / 2.0);
        double fMax = SpeedOfLightNmPerS / (CenterWavelengthNm - SpanNm / 2.0);
        double tauGroupDelay = 5 / (fMax - fMin);
        var builder = CreateTwoPortBuilder(inputPin, outputPin, wavelengthNm =>
        {
            double freq = SpeedOfLightNmPerS / wavelengthNm;
            return Complex.Exp(new Complex(0, -2 * Math.PI * freq * tauGroupDelay));
        });

        var timeDef = TimeSignalDefinition.FromWavelengthSweep(CenterWavelengthNm, SpanNm, NPoints);
        var input = timeDef.CreateGaussianPulse(10 * timeDef.TimeStepSeconds, 2 * timeDef.TimeStepSeconds);
        var inputSignals = new Dictionary<Guid, double[]> { { inputPin, input } };

        var batch = new TimeDomainSimulator(builder.Object)
            .Run(inputSignals, timeDef, CenterWavelengthNm, SpanNm, NPoints);
        var stepped = new SampleModeTransientDriver(builder.Object)
            .Run(inputSignals, timeDef, null, CenterWavelengthNm, SpanNm, NPoints);

        AssertTracesMatch(batch, stepped, outputPin);
    }

    [Fact]
    public void LosslessSplitter_PrbsInput_MatchesBatchSimulatorOnBothArms()
    {
        var inputPin = Guid.NewGuid();
        var out1 = Guid.NewGuid();
        var out2 = Guid.NewGuid();
        var builder = CreateSplitterBuilder(inputPin, out1, out2);

        var timeDef = SamplingPolicy.CreateGrid(
            25e9, samplesPerSymbol: 16, symbolCount: 8, guardSamples: NPoints);
        var input = new PrbsSource(25e9, prbsOrder: 7, seed: 3).Generate(timeDef);
        var inputSignals = new Dictionary<Guid, double[]> { { inputPin, input } };

        var batch = new TimeDomainSimulator(builder.Object)
            .Run(inputSignals, timeDef, CenterWavelengthNm, SpanNm, NPoints);
        var stepped = new SampleModeTransientDriver(builder.Object)
            .Run(inputSignals, timeDef, null, CenterWavelengthNm, SpanNm, NPoints);

        AssertTracesMatch(batch, stepped, out1);
        AssertTracesMatch(batch, stepped, out2);
    }

    [Fact]
    public void LosslessSplitter_CwInput_ConservesEnergyInSteadyState()
    {
        var inputPin = Guid.NewGuid();
        var out1 = Guid.NewGuid();
        var out2 = Guid.NewGuid();
        var builder = CreateSplitterBuilder(inputPin, out1, out2);

        var timeDef = TimeSignalDefinition.FromWavelengthSweep(CenterWavelengthNm, SpanNm, 256);
        var input = new CwSource(amplitude: 1.0).Generate(timeDef);
        var inputSignals = new Dictionary<Guid, double[]> { { inputPin, input } };

        var result = new SampleModeTransientDriver(builder.Object)
            .Run(inputSignals, timeDef, null, CenterWavelengthNm, SpanNm, 256);

        // After the impulse response has fully filled (n ≥ IR length), the
        // summed output intensity must equal the CW input power (= 1).
        var trace1 = result.PinTraces[out1];
        var trace2 = result.PinTraces[out2];
        for (int n = 200; n < timeDef.NSamples; n++)
            (trace1[n] + trace2[n]).ShouldBe(1.0, 0.05, $"steady-state sample {n}");
    }

    [Fact]
    public void PulseAtWindowEnd_GuardSamplesPreserveTailEnergy()
    {
        var inputPin = Guid.NewGuid();
        var outputPin = Guid.NewGuid();
        var builder = CreateTwoPortBuilder(inputPin, outputPin, _ => Complex.One);

        // Pulse centred on the LAST symbol; the guard (≥ IR length) must give
        // the convolution tail room so no pulse energy is truncated (§5.6).
        var timeDef = SamplingPolicy.CreateGrid(
            25e9, samplesPerSymbol: 16, symbolCount: 8, guardSamples: NPoints);
        int lastSymbolCentre = 16 * 8 - 8;
        var input = new PulseSource(
            lastSymbolCentre * timeDef.TimeStepSeconds,
            2 * timeDef.TimeStepSeconds).Generate(timeDef);
        var inputSignals = new Dictionary<Guid, double[]> { { inputPin, input } };

        var result = new SampleModeTransientDriver(builder.Object)
            .Run(inputSignals, timeDef, null, CenterWavelengthNm, SpanNm, NPoints);

        double inputEnergy = input.Sum(v => v * v);
        double outputEnergy = result.PinTraces[outputPin].Sum();
        outputEnergy.ShouldBe(inputEnergy, inputEnergy * 0.02,
            "the full pulse energy must arrive within the guarded window");
    }

    [Fact]
    public void OutputSteppable_IsAppliedPerSample()
    {
        var inputPin = Guid.NewGuid();
        var outputPin = Guid.NewGuid();
        var builder = CreateTwoPortBuilder(inputPin, outputPin, _ => Complex.One);

        var timeDef = TimeSignalDefinition.FromWavelengthSweep(CenterWavelengthNm, SpanNm, NPoints);
        var input = timeDef.CreateGaussianPulse(20 * timeDef.TimeStepSeconds, 3 * timeDef.TimeStepSeconds);
        var inputSignals = new Dictionary<Guid, double[]> { { inputPin, input } };

        var passive = new SampleModeTransientDriver(builder.Object)
            .Run(inputSignals, timeDef, null, CenterWavelengthNm, SpanNm, NPoints);
        var withGain = new SampleModeTransientDriver(builder.Object)
            .Run(inputSignals, timeDef,
                new Dictionary<Guid, ITimeSteppable> { { outputPin, new FieldGainSteppable(2.0) } },
                CenterWavelengthNm, SpanNm, NPoints);

        // Field gain 2 → intensity gain 4 at every sample.
        var passiveTrace = passive.PinTraces[outputPin];
        var gainTrace = withGain.PinTraces[outputPin];
        for (int n = 0; n < timeDef.NSamples; n++)
            gainTrace[n].ShouldBe(4.0 * passiveTrace[n], 1e-9, $"sample {n}");
    }

    [Fact]
    public void Run_NullArguments_Throw()
    {
        var builder = CreateTwoPortBuilder(Guid.NewGuid(), Guid.NewGuid(), _ => Complex.One);
        var driver = new SampleModeTransientDriver(builder.Object);
        var timeDef = TimeSignalDefinition.FromWavelengthSweep(CenterWavelengthNm, SpanNm, NPoints);

        Should.Throw<ArgumentNullException>(() => driver.Run(null!, timeDef));
        Should.Throw<ArgumentNullException>(() => driver.Run(new Dictionary<Guid, double[]>(), null!));
        Should.Throw<ArgumentNullException>(() => new SampleModeTransientDriver(null!));
    }

    /// <summary>Test steppable: multiplies the field by a constant gain.</summary>
    private sealed class FieldGainSteppable : ITimeSteppable
    {
        private readonly double _gain;

        public FieldGainSteppable(double gain) => _gain = gain;

        public void Step(ReadOnlySpan<Complex> inputs, Span<Complex> outputs, double dt)
            => outputs[0] = inputs[0] * _gain;
    }
}
