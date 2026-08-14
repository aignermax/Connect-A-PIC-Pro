using CAP_Core.LightCalculation.TimeDomainSimulation.PhaseNoise;
using Shouldly;
using Xunit;

namespace UnitTests.LightCalculation.TimeDomainSimulation.PhaseNoise;

/// <summary>
/// Issue #834: the Wiener phase walk must reproduce the Lorentzian-linewidth
/// statistics — Var[φ(t)] = 2π·Δν·t and coherence decay e^{−π·Δν·τ}.
/// </summary>
public class WienerPhaseProcessTests
{
    private const double LinewidthHz = 1e9;
    private const double DtSeconds = 1e-12;

    [Fact]
    public void Generate_ZeroLinewidth_ReturnsAllZeroPhases()
    {
        var phases = WienerPhaseProcess.Generate(0, DtSeconds, 100, new Random(1));
        phases.ShouldAllBe(p => p == 0);
    }

    [Fact]
    public void Generate_SameSeed_IsDeterministic()
    {
        var a = WienerPhaseProcess.Generate(LinewidthHz, DtSeconds, 50, new Random(42));
        var b = WienerPhaseProcess.Generate(LinewidthHz, DtSeconds, 50, new Random(42));
        a.ShouldBe(b);
    }

    [Fact]
    public void Generate_PhaseVarianceGrowsAs2PiLinewidthTimesTime()
    {
        const int nSamples = 200;
        const int trials = 2000;
        double expectedVariance = 2 * Math.PI * LinewidthHz * (nSamples - 1) * DtSeconds;

        var finals = new double[trials];
        for (int t = 0; t < trials; t++)
            finals[t] = WienerPhaseProcess
                .Generate(LinewidthHz, DtSeconds, nSamples, new Random(1000 + t))[nSamples - 1];

        double mean = finals.Average();
        double variance = finals.Sum(v => (v - mean) * (v - mean)) / (trials - 1);
        variance.ShouldBe(expectedVariance, expectedVariance * 0.15);
    }

    [Fact]
    public void Generate_CoherenceDecaysAsExpMinusPiLinewidthTau()
    {
        // E[cos(φ(τ) − φ(0))] = e^{−π·Δν·τ}; choose π·Δν·τ ≈ 0.314.
        const int lagSamples = 100;
        const int trials = 3000;
        double tau = lagSamples * DtSeconds;
        double expected = Math.Exp(-Math.PI * LinewidthHz * tau);

        double sum = 0;
        for (int t = 0; t < trials; t++)
        {
            var phases = WienerPhaseProcess
                .Generate(LinewidthHz, DtSeconds, lagSamples + 1, new Random(5000 + t));
            sum += Math.Cos(phases[lagSamples]);
        }

        (sum / trials).ShouldBe(expected, 0.05);
    }

    [Fact]
    public void CoherenceTimeSeconds_MatchesOneOverPiLinewidth()
    {
        WienerPhaseProcess.CoherenceTimeSeconds(LinewidthHz)
            .ShouldBe(1.0 / (Math.PI * LinewidthHz), 1e-15);
        WienerPhaseProcess.CoherenceTimeSeconds(0).ShouldBe(double.PositiveInfinity);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(-1e-12, 100)]
    [InlineData(1e-12, 0)]
    public void Generate_InvalidArguments_Throw(double dt, int nSamples)
    {
        Should.Throw<ArgumentOutOfRangeException>(
            () => WienerPhaseProcess.Generate(LinewidthHz, dt, nSamples, new Random(1)));
    }

    [Fact]
    public void Generate_NullRng_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => WienerPhaseProcess.Generate(LinewidthHz, DtSeconds, 10, null!));
    }
}
