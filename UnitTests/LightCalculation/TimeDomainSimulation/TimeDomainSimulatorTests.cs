using CAP_Core.LightCalculation;
using CAP_Core.LightCalculation.TimeDomainSimulation;
using CAP_Core.Components.FormulaReading;
using Moq;
using Shouldly;
using System.Numerics;
using Xunit;

namespace UnitTests.LightCalculation.TimeDomainSimulation;

public class TimeDomainSimulatorTests
{
    private const double SpeedOfLightNmPerS = 2.998e17;
    private const double CenterWavelengthNm = 1550;
    private const double SpanNm = 100;
    private const int NPoints = 64;

    // Bandwidth of the sweep
    private static double ComputeBandwidth()
    {
        double fMin = SpeedOfLightNmPerS / (CenterWavelengthNm + SpanNm / 2.0);
        double fMax = SpeedOfLightNmPerS / (CenterWavelengthNm - SpanNm / 2.0);
        return fMax - fMin;
    }

    private static SMatrix CreateTwoPortMatrix(Guid inputPin, Guid outputPin, Complex s21)
    {
        var allPins = new List<Guid> { inputPin, outputPin };
        var matrix = new SMatrix(allPins, new());
        matrix.SetValues(new Dictionary<(Guid, Guid), Complex>
        {
            { (inputPin, outputPin), s21 }
        });
        return matrix;
    }

    private static SMatrix CreateFourPortCouplerMatrix(
        Guid in1, Guid in2, Guid out1, Guid out2, double coupling)
    {
        var allPins = new List<Guid> { in1, in2, out1, out2 };
        var matrix = new SMatrix(allPins, new());
        matrix.SetValues(new Dictionary<(Guid, Guid), Complex>
        {
            { (in1, out1), coupling },
            { (in1, out2), coupling },
            { (in2, out1), coupling },
            { (in2, out2), coupling },
        });
        return matrix;
    }

    [Fact]
    public void Run_StraightWaveguide_OutputIntensityMatchesInputPower()
    {
        // Arrange: S_21 = 1 → h[0] = 1, output = input
        var inputPin = Guid.NewGuid();
        var outputPin = Guid.NewGuid();

        var mockBuilder = new Mock<ISystemMatrixBuilder>();
        mockBuilder.Setup(b => b.GetSystemSMatrix(It.IsAny<int>()))
            .Returns((int _) => CreateTwoPortMatrix(inputPin, outputPin, Complex.One));

        var timeDef = TimeSignalDefinition.FromWavelengthSweep(CenterWavelengthNm, SpanNm, NPoints);
        // Gaussian pulse centered at 20 samples
        double centerTime = 20 * timeDef.TimeStepSeconds;
        double sigma = 3 * timeDef.TimeStepSeconds;
        var inputSignal = timeDef.CreateGaussianPulse(centerTime, sigma);

        var inputSignals = new Dictionary<Guid, double[]> { { inputPin, inputSignal } };

        var simulator = new TimeDomainSimulator(mockBuilder.Object);

        // Act
        var result = simulator.Run(inputSignals, timeDef, CenterWavelengthNm, SpanNm, NPoints);

        // Assert: output pin trace exists
        result.PinTraces.ShouldContainKey(outputPin);

        // Peak input power = signal[20]^2 → output intensity at same sample ≈ signal[20]^2
        double inputPeak = inputSignal[20] * inputSignal[20];
        double outputPeak = result.PinTraces[outputPin][20];
        outputPeak.ShouldBe(inputPeak, inputPeak * 0.01); // within 1% tolerance
    }

    [Fact]
    public void Run_HalfPowerCoupler_EnergyConserved()
    {
        // Arrange: 50:50 coupler — input splits equally to two outputs
        var inputPin = Guid.NewGuid();
        var dummyIn2 = Guid.NewGuid();
        var outputPin1 = Guid.NewGuid();
        var outputPin2 = Guid.NewGuid();

        double coupling = 1.0 / Math.Sqrt(2.0); // |S|=1/√2 → power = 0.5

        var mockBuilder = new Mock<ISystemMatrixBuilder>();
        mockBuilder.Setup(b => b.GetSystemSMatrix(It.IsAny<int>()))
            .Returns((int _) =>
                CreateFourPortCouplerMatrix(inputPin, dummyIn2, outputPin1, outputPin2, coupling));

        var timeDef = TimeSignalDefinition.FromWavelengthSweep(CenterWavelengthNm, SpanNm, NPoints);
        double centerTime = 20 * timeDef.TimeStepSeconds;
        double sigma = 3 * timeDef.TimeStepSeconds;
        var inputSignal = timeDef.CreateGaussianPulse(centerTime, sigma);

        var inputSignals = new Dictionary<Guid, double[]> { { inputPin, inputSignal } };

        var simulator = new TimeDomainSimulator(mockBuilder.Object);

        // Act
        var result = simulator.Run(inputSignals, timeDef, CenterWavelengthNm, SpanNm, NPoints);

        // Assert: both outputs present
        result.PinTraces.ShouldContainKey(outputPin1);
        result.PinTraces.ShouldContainKey(outputPin2);

        // Energy conservation: sum of output intensities ≈ input intensity
        double inputPower = inputSignal.Sum(v => v * v);
        double output1Power = result.PinTraces[outputPin1].Sum();
        double output2Power = result.PinTraces[outputPin2].Sum();

        double totalOutputPower = output1Power + output2Power;
        // Each coupler arm gets 0.5 * input, so total = input (within 5% tolerance)
        totalOutputPower.ShouldBe(inputPower, inputPower * 0.05);
    }

    [Fact]
    public void Run_WithNonlinearCircuit_ThrowsInvalidOperationException()
    {
        var inputPin = Guid.NewGuid();
        var outputPin = Guid.NewGuid();
        var allPins = new List<Guid> { inputPin, outputPin };

        var mockBuilder = new Mock<ISystemMatrixBuilder>();
        mockBuilder.Setup(b => b.GetSystemSMatrix(It.IsAny<int>()))
            .Returns((int _) =>
            {
                var matrix = new SMatrix(allPins, new());
                var nonLinFn = new ConnectionFunction(
                    _ => Complex.One, "abs(P1)", new List<Guid> { inputPin },
                    IsInnerLoopFunction: false);
                matrix.NonLinearConnections.Add((inputPin, outputPin), nonLinFn);
                return matrix;
            });

        var timeDef = TimeSignalDefinition.FromWavelengthSweep(CenterWavelengthNm, SpanNm, NPoints);
        var inputSignals = new Dictionary<Guid, double[]>
        {
            { inputPin, new double[NPoints] }
        };

        var simulator = new TimeDomainSimulator(mockBuilder.Object);

        Should.Throw<InvalidOperationException>(() =>
            simulator.Run(inputSignals, timeDef, CenterWavelengthNm, SpanNm, NPoints));
    }

    [Fact]
    public void Run_ParametricSplitter_MatchesFixedValueDesignBitExactly()
    {
        // Arrange: a slider-bound MMI-style splitter (parametric like the demo PDK
        // since #825) vs. the same splitter with fixed values — transient traces
        // must be identical because slider parameters are constants during a run.
        var inputPin = Guid.NewGuid();
        var outputPin1 = Guid.NewGuid();
        var outputPin2 = Guid.NewGuid();
        var sliderId = Guid.NewGuid();
        const double splittingRatioPercent = 65.0;
        var allPins = new List<Guid> { inputPin, outputPin1, outputPin2 };

        var parametricBuilder = new Mock<ISystemMatrixBuilder>();
        parametricBuilder.Setup(b => b.GetSystemSMatrix(It.IsAny<int>()))
            .Returns((int _) =>
            {
                var matrix = new SMatrix(allPins, new() { (sliderId, splittingRatioPercent) });
                var toOut1 = new ConnectionFunction(
                    p => new Complex(Math.Sqrt((double)p[0] / 100.0), 0),
                    "sqrt(SR/100)", new List<Guid> { sliderId }, IsInnerLoopFunction: false);
                var toOut2 = new ConnectionFunction(
                    p => new Complex(Math.Sqrt(1.0 - (double)p[0] / 100.0), 0),
                    "sqrt(1-SR/100)", new List<Guid> { sliderId }, IsInnerLoopFunction: false);
                matrix.NonLinearConnections.Add((inputPin, outputPin1), toOut1);
                matrix.NonLinearConnections.Add((inputPin, outputPin2), toOut2);
                return matrix;
            });

        var fixedBuilder = new Mock<ISystemMatrixBuilder>();
        fixedBuilder.Setup(b => b.GetSystemSMatrix(It.IsAny<int>()))
            .Returns((int _) =>
            {
                var matrix = new SMatrix(allPins, new());
                matrix.SetValues(new Dictionary<(Guid, Guid), Complex>
                {
                    { (inputPin, outputPin1), new Complex(Math.Sqrt(splittingRatioPercent / 100.0), 0) },
                    { (inputPin, outputPin2), new Complex(Math.Sqrt(1.0 - splittingRatioPercent / 100.0), 0) },
                });
                return matrix;
            });

        var timeDef = TimeSignalDefinition.FromWavelengthSweep(CenterWavelengthNm, SpanNm, NPoints);
        var inputSignal = timeDef.CreateGaussianPulse(
            20 * timeDef.TimeStepSeconds, 3 * timeDef.TimeStepSeconds);
        var inputSignals = new Dictionary<Guid, double[]> { { inputPin, inputSignal } };

        // Act
        var parametricResult = new TimeDomainSimulator(parametricBuilder.Object)
            .Run(inputSignals, timeDef, CenterWavelengthNm, SpanNm, NPoints);
        var fixedResult = new TimeDomainSimulator(fixedBuilder.Object)
            .Run(inputSignals, timeDef, CenterWavelengthNm, SpanNm, NPoints);

        // Assert: bit-exact traces at both outputs
        foreach (var outputPin in new[] { outputPin1, outputPin2 })
        {
            parametricResult.PinTraces.ShouldContainKey(outputPin);
            var parametricTrace = parametricResult.PinTraces[outputPin];
            var fixedTrace = fixedResult.PinTraces[outputPin];
            for (int i = 0; i < parametricTrace.Length; i++)
                parametricTrace[i].ShouldBe(fixedTrace[i]);
        }
    }

    [Fact]
    public void Run_NullInputSignals_ThrowsArgumentNullException()
    {
        var mockBuilder = new Mock<ISystemMatrixBuilder>();
        var simulator = new TimeDomainSimulator(mockBuilder.Object);
        var timeDef = TimeSignalDefinition.FromWavelengthSweep(CenterWavelengthNm, SpanNm, NPoints);

        Should.Throw<ArgumentNullException>(() =>
            simulator.Run(null!, timeDef));
    }

    [Fact]
    public void Run_NullTimeDef_ThrowsArgumentNullException()
    {
        var mockBuilder = new Mock<ISystemMatrixBuilder>();
        var simulator = new TimeDomainSimulator(mockBuilder.Object);

        Should.Throw<ArgumentNullException>(() =>
            simulator.Run(new Dictionary<Guid, double[]>(), null!));
    }

    [Fact]
    public void Constructor_NullMatrixBuilder_ThrowsArgumentNullException()
    {
        Should.Throw<ArgumentNullException>(() => new TimeDomainSimulator(null!));
    }

    [Fact]
    public void Run_GroupDelayedWaveguide_OutputPeakShiftedByGroupDelay()
    {
        // Arrange: S_21(f) = exp(-2πi * f * tau_g) → 5-sample group delay
        var inputPin = Guid.NewGuid();
        var outputPin = Guid.NewGuid();

        double bandwidth = ComputeBandwidth();
        int delayInSamples = 5;
        double tauGroupDelay = delayInSamples / bandwidth;

        var mockBuilder = new Mock<ISystemMatrixBuilder>();
        mockBuilder.Setup(b => b.GetSystemSMatrix(It.IsAny<int>()))
            .Returns((int wavelengthNm) =>
            {
                double freq = SpeedOfLightNmPerS / wavelengthNm;
                Complex s21 = Complex.Exp(new Complex(0, -2 * Math.PI * freq * tauGroupDelay));
                return CreateTwoPortMatrix(inputPin, outputPin, s21);
            });

        var timeDef = TimeSignalDefinition.FromWavelengthSweep(CenterWavelengthNm, SpanNm, NPoints);
        // Place input pulse at sample 10 so the delayed output (at sample 15) is within window
        double centerTime = 10 * timeDef.TimeStepSeconds;
        double sigma = 2 * timeDef.TimeStepSeconds;
        var inputSignal = timeDef.CreateGaussianPulse(centerTime, sigma);

        var inputSignals = new Dictionary<Guid, double[]> { { inputPin, inputSignal } };
        var simulator = new TimeDomainSimulator(mockBuilder.Object);

        // Act
        var result = simulator.Run(inputSignals, timeDef, CenterWavelengthNm, SpanNm, NPoints);

        // Assert: output intensity peak should be at sample 10 + 5 = 15 (± 1)
        var trace = result.PinTraces[outputPin];
        int inputPeakIdx = Array.IndexOf(inputSignal, inputSignal.Max());
        int outputPeakIdx = Array.IndexOf(trace, trace.Max());

        int observedDelay = outputPeakIdx - inputPeakIdx;
        Math.Abs(observedDelay - delayInSamples).ShouldBeLessThanOrEqualTo(2,
            $"Expected group delay of {delayInSamples} samples, measured {observedDelay}");
    }
}
