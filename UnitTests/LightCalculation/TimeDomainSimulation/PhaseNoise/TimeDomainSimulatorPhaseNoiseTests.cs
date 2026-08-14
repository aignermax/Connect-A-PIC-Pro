using CAP_Core.LightCalculation;
using CAP_Core.LightCalculation.TimeDomainSimulation;
using CAP_Core.LightCalculation.TimeDomainSimulation.PhaseNoise;
using Moq;
using Shouldly;
using System.Numerics;
using Xunit;

namespace UnitTests.LightCalculation.TimeDomainSimulation.PhaseNoise;

/// <summary>
/// Issue #834: the optional phase-noise model of <see cref="TimeDomainSimulator.Run"/> —
/// zero linewidth reproduces the baseline exactly, a single flat path carries no
/// intensity noise, and two independent lasers combined on one output beat visibly.
/// </summary>
public class TimeDomainSimulatorPhaseNoiseTests
{
    private const double CenterWavelengthNm = 1550;
    private const double SpanNm = 100;
    private const int NPoints = 64;
    private const double LinewidthHz = 2e11;

    private static SMatrix CreateTwoPortMatrix(Guid inputPin, Guid outputPin, Complex s21)
    {
        var matrix = new SMatrix(new List<Guid> { inputPin, outputPin }, new());
        matrix.SetValues(new Dictionary<(Guid, Guid), Complex> { { (inputPin, outputPin), s21 } });
        return matrix;
    }

    private static Mock<ISystemMatrixBuilder> MockTwoPort(Guid inputPin, Guid outputPin)
    {
        var mockBuilder = new Mock<ISystemMatrixBuilder>();
        mockBuilder.Setup(b => b.GetSystemSMatrix(It.IsAny<int>()))
            .Returns((int _) => CreateTwoPortMatrix(inputPin, outputPin, Complex.One));
        return mockBuilder;
    }

    [Fact]
    public void Run_ZeroLinewidthSettings_MatchesBaselineExactly()
    {
        var inputPin = Guid.NewGuid();
        var outputPin = Guid.NewGuid();
        var mockBuilder = MockTwoPort(inputPin, outputPin);

        var timeDef = TimeSignalDefinition.FromWavelengthSweep(CenterWavelengthNm, SpanNm, NPoints);
        var inputSignal = timeDef.CreateGaussianPulse(
            20 * timeDef.TimeStepSeconds, 3 * timeDef.TimeStepSeconds);
        var inputSignals = new Dictionary<Guid, double[]> { { inputPin, inputSignal } };

        var idealNoise = new PhaseNoiseSettings();
        idealNoise.AddSource(inputPin, new PhaseNoiseSource(0, "laser1"));

        var simulator = new TimeDomainSimulator(mockBuilder.Object);
        var baseline = simulator.Run(inputSignals, timeDef, CenterWavelengthNm, SpanNm, NPoints);
        var withIdeal = simulator.Run(
            inputSignals, timeDef, CenterWavelengthNm, SpanNm, NPoints, idealNoise);

        withIdeal.PinTraces[outputPin].ShouldBe(baseline.PinTraces[outputPin]);
    }

    [Fact]
    public void Run_SinglePathWithPhaseNoise_IntensityUnaffected()
    {
        // Phase noise alone carries no intensity noise through one path: |x·e^{iφ}|² = |x|².
        var inputPin = Guid.NewGuid();
        var outputPin = Guid.NewGuid();
        var mockBuilder = MockTwoPort(inputPin, outputPin);

        var timeDef = TimeSignalDefinition.FromWavelengthSweep(CenterWavelengthNm, SpanNm, NPoints);
        var inputSignal = timeDef.CreateGaussianPulse(
            20 * timeDef.TimeStepSeconds, 3 * timeDef.TimeStepSeconds);
        var inputSignals = new Dictionary<Guid, double[]> { { inputPin, inputSignal } };

        var noise = new PhaseNoiseSettings();
        noise.AddSource(inputPin, new PhaseNoiseSource(LinewidthHz, "laser1"));

        var simulator = new TimeDomainSimulator(mockBuilder.Object);
        var result = simulator.Run(
            inputSignals, timeDef, CenterWavelengthNm, SpanNm, NPoints, noise);

        double inputPeak = inputSignal[20] * inputSignal[20];
        result.PinTraces[outputPin][20].ShouldBe(inputPeak, inputPeak * 0.01);
    }

    [Fact]
    public void Run_TwoIndependentLasersOnOnePort_ProduceBeatNoise()
    {
        // Two inputs combine on one output; independent phase walks make the
        // coherent sum fluctuate, while zero linewidth keeps it constant.
        var input1 = Guid.NewGuid();
        var input2 = Guid.NewGuid();
        var outputPin = Guid.NewGuid();
        double coupling = 1.0 / Math.Sqrt(2.0);

        var mockBuilder = new Mock<ISystemMatrixBuilder>();
        mockBuilder.Setup(b => b.GetSystemSMatrix(It.IsAny<int>()))
            .Returns((int _) =>
            {
                var matrix = new SMatrix(new List<Guid> { input1, input2, outputPin }, new());
                matrix.SetValues(new Dictionary<(Guid, Guid), Complex>
                {
                    { (input1, outputPin), coupling },
                    { (input2, outputPin), coupling },
                });
                return matrix;
            });

        var timeDef = TimeSignalDefinition.FromWavelengthSweep(CenterWavelengthNm, SpanNm, NPoints);
        var cw = Enumerable.Repeat(1.0, timeDef.NSamples).ToArray();
        var inputSignals = new Dictionary<Guid, double[]> { { input1, cw }, { input2, cw } };

        var noise = new PhaseNoiseSettings();
        noise.AddSource(input1, new PhaseNoiseSource(LinewidthHz, "laserA"));
        noise.AddSource(input2, new PhaseNoiseSource(LinewidthHz, "laserB"));

        var simulator = new TimeDomainSimulator(mockBuilder.Object);
        var result = simulator.Run(
            inputSignals, timeDef, CenterWavelengthNm, SpanNm, NPoints, noise);

        // Skip the convolution turn-on; the steady region must fluctuate (beat noise).
        var steady = result.PinTraces[outputPin].Skip(10).ToArray();
        double mean = steady.Average();
        double variance = steady.Sum(v => (v - mean) * (v - mean)) / steady.Length;

        variance.ShouldBeGreaterThan(1e-4, "independent lasers must beat");
        // Both walks start at φ = 0 (fully coherent, I = 2) and decorrelate over the
        // window, so the mean must have dropped visibly below full coherence.
        mean.ShouldBeInRange(0.2, 1.95);
        steady.ShouldAllBe(v => v >= 0 && v <= 2.1);
    }
}
