using CAP_Core.LightCalculation;
using CAP_Core.LightCalculation.TimeDomainSimulation;
using CAP_Core.LightCalculation.TimeDomainSimulation.CompactModels;
using CAP_Core.LightCalculation.TimeDomainSimulation.CompactModels.Models;
using Moq;
using Shouldly;
using System.Numerics;
using Xunit;

namespace UnitTests.LightCalculation.TimeDomainSimulation.CompactModels;

/// <summary>
/// End-to-end mixed passive/active test (issue #529 acceptance): a laser diode
/// source drives a passive waveguide; a photodiode sink converts the arriving
/// field to a photocurrent. The chain must be stable and reproduce the
/// analytic DC operating point.
/// </summary>
public class MixedSignalTimeDomainSimulatorTests
{
    private const double SampleRateHz = 1e12;  // dt = 1 ps
    private const int NSamples = 4096;         // ≈ 4 ns
    private const double WaveguideAmplitude = 0.7; // |S21| → power ×0.49
    private const double Responsivity = 0.8;

    private static Mock<ISystemMatrixBuilder> CreateWaveguideNetwork(Guid inPin, Guid outPin)
    {
        var mockBuilder = new Mock<ISystemMatrixBuilder>();
        mockBuilder.Setup(b => b.GetSystemSMatrix(It.IsAny<int>()))
            .Returns((int _) =>
            {
                var matrix = new SMatrix(new List<Guid> { inPin, outPin }, new());
                matrix.SetValues(new Dictionary<(Guid, Guid), Complex>
                {
                    { (inPin, outPin), new Complex(WaveguideAmplitude, 0) },
                });
                return matrix;
            });
        return mockBuilder;
    }

    private static double MeanOfLastTenPercent(double[] trace)
        => trace.Skip(trace.Length * 9 / 10).Average();

    [Fact]
    public void Run_LaserThroughWaveguideIntoPhotodiode_ReproducesDcOperatingPoint()
    {
        // Arrange: laser → waveguide (|S21| = 0.7) → photodiode
        var laserPin = Guid.NewGuid();
        var photodiodePin = Guid.NewGuid();
        var network = CreateWaveguideNetwork(laserPin, photodiodePin);

        var laser = new LaserDiodeRateEquationModel();
        double driveAmps = 2.0 * laser.ThresholdCurrentAmps;
        var drive = Enumerable.Repeat(driveAmps, NSamples).ToArray();

        var photodiode = new PhotodiodeRcModel(new Dictionary<string, double>
        {
            [PhotodiodeRcModel.ResponsivityKey] = Responsivity,
        });

        var timeDef = new TimeSignalDefinition(SampleRateHz, NSamples);
        var simulator = new MixedSignalTimeDomainSimulator(network.Object);

        // Act
        var result = simulator.Run(
            opticalInputSignals: new Dictionary<Guid, double[]>(),
            sourceModels: new Dictionary<Guid, ActiveSource>
            {
                [laserPin] = new ActiveSource(laser, drive),
            },
            sinkModels: new Dictionary<Guid, ICompactModel>
            {
                [photodiodePin] = photodiode,
            },
            timeDef: timeDef);

        // Assert: both electrical traces exist (laser power + photocurrent)
        result.ElectricalTraces.ShouldContainKey(laserPin);
        result.ElectricalTraces.ShouldContainKey(photodiodePin);

        var laserPower = result.ElectricalTraces[laserPin];
        var photocurrent = result.ElectricalTraces[photodiodePin];

        // Stability: no NaN / Infinity / negative anywhere in the chain.
        laserPower.ShouldAllBe(p => double.IsFinite(p) && p >= 0);
        photocurrent.ShouldAllBe(i => double.IsFinite(i) && i >= 0);

        // DC operating point: i_pd ≈ R · |S21|² · P_laser (within 10%).
        double steadyLaserPower = MeanOfLastTenPercent(laserPower);
        double steadyPhotocurrent = MeanOfLastTenPercent(photocurrent);
        double expected = Responsivity * WaveguideAmplitude * WaveguideAmplitude * steadyLaserPower;

        steadyLaserPower.ShouldBeGreaterThan(0);
        steadyPhotocurrent.ShouldBe(expected, expected * 0.10);
    }

    [Fact]
    public void Run_PhotodiodeAbsorbsLight_OpticalTraceAtSinkIsZero()
    {
        var laserPin = Guid.NewGuid();
        var photodiodePin = Guid.NewGuid();
        var network = CreateWaveguideNetwork(laserPin, photodiodePin);

        var laser = new LaserDiodeRateEquationModel();
        var drive = Enumerable.Repeat(2.0 * laser.ThresholdCurrentAmps, NSamples).ToArray();
        var timeDef = new TimeSignalDefinition(SampleRateHz, NSamples);
        var simulator = new MixedSignalTimeDomainSimulator(network.Object);

        var result = simulator.Run(
            new Dictionary<Guid, double[]>(),
            new Dictionary<Guid, ActiveSource> { [laserPin] = new ActiveSource(laser, drive) },
            new Dictionary<Guid, ICompactModel> { [photodiodePin] = new PhotodiodeRcModel() },
            timeDef);

        result.OpticalTraces[photodiodePin].ShouldAllBe(intensity => intensity == 0.0);
    }

    [Fact]
    public void Run_PassiveOnlyInput_BehavesLikePassiveSimulation()
    {
        // No active models at all: a Gaussian pulse through the waveguide.
        var inPin = Guid.NewGuid();
        var outPin = Guid.NewGuid();
        var network = CreateWaveguideNetwork(inPin, outPin);

        var timeDef = new TimeSignalDefinition(SampleRateHz, 256);
        var pulse = timeDef.CreateGaussianPulse(
            20 * timeDef.TimeStepSeconds, 3 * timeDef.TimeStepSeconds);

        var simulator = new MixedSignalTimeDomainSimulator(network.Object);
        var result = simulator.Run(
            new Dictionary<Guid, double[]> { [inPin] = pulse },
            new Dictionary<Guid, ActiveSource>(),
            new Dictionary<Guid, ICompactModel>(),
            timeDef);

        result.ElectricalTraces.ShouldBeEmpty();
        double expectedPeak = WaveguideAmplitude * WaveguideAmplitude * pulse[20] * pulse[20];
        result.OpticalTraces[outPin][20].ShouldBe(expectedPeak, expectedPeak * 0.01);
    }

    [Fact]
    public void Run_NullArguments_Throw()
    {
        var network = CreateWaveguideNetwork(Guid.NewGuid(), Guid.NewGuid());
        var simulator = new MixedSignalTimeDomainSimulator(network.Object);
        var timeDef = new TimeSignalDefinition(SampleRateHz, 16);
        var empty = new Dictionary<Guid, double[]>();
        var noSources = new Dictionary<Guid, ActiveSource>();
        var noSinks = new Dictionary<Guid, ICompactModel>();

        Should.Throw<ArgumentNullException>(() => new MixedSignalTimeDomainSimulator(null!));
        Should.Throw<ArgumentNullException>(() => simulator.Run(null!, noSources, noSinks, timeDef));
        Should.Throw<ArgumentNullException>(() => simulator.Run(empty, null!, noSinks, timeDef));
        Should.Throw<ArgumentNullException>(() => simulator.Run(empty, noSources, null!, timeDef));
        Should.Throw<ArgumentNullException>(() => simulator.Run(empty, noSources, noSinks, null!));
    }
}
