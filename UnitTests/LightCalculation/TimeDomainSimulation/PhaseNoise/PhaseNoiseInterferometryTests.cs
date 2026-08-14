using CAP_Core.LightCalculation.TimeDomainSimulation;
using CAP_Core.LightCalculation.TimeDomainSimulation.PhaseNoise;
using Shouldly;
using System.Numerics;
using Xunit;

namespace UnitTests.LightCalculation.TimeDomainSimulation.PhaseNoise;

/// <summary>
/// Issue #834 physics validation: a balanced MZI shows no linewidth penalty, an
/// unbalanced MZI's extinction degrades as e^{−π·Δν·Δτ}, and the beat of two
/// independent lasers carries the COMBINED linewidth Δν1 + Δν2.
/// </summary>
public class PhaseNoiseInterferometryTests
{
    private const double LinewidthHz = 1e9;
    private const double DtSeconds = 1e-12;

    /// <summary>CW unit field carrying the Wiener phase factor e^{iφ(t)}.</summary>
    private static Complex[] NoisyCwField(double linewidthHz, int nSamples, int seed)
    {
        var phases = WienerPhaseProcess.Generate(linewidthHz, DtSeconds, nSamples, new Random(seed));
        return phases.Select(p => Complex.FromPolarCoordinates(1, p)).ToArray();
    }

    /// <summary>Two-arm interferometer h(t) = 0.5·δ[d1] + 0.5·δ[d2].</summary>
    private static Complex[] MziImpulseResponse(int d1, int d2)
    {
        var h = new Complex[Math.Max(d1, d2) + 1];
        h[d1] += 0.5;
        h[d2] += 0.5;
        return h;
    }

    [Fact]
    public void BalancedMzi_HasNoLinewidthPenalty()
    {
        // Equal arm delays: the common-mode phase cancels → constant unit intensity.
        const int nSamples = 5000;
        const int delay = 20;
        var input = NoisyCwField(LinewidthHz, nSamples, seed: 1);
        var output = TimeDomainConvolver.Convolve(input, MziImpulseResponse(delay, delay));

        for (int n = delay; n < nSamples; n++)
            (output[n].Real * output[n].Real + output[n].Imaginary * output[n].Imaginary)
                .ShouldBe(1.0, 1e-9);
    }

    [Fact]
    public void UnbalancedMzi_ExtinctionDegradesWithLinewidthTimesDelay()
    {
        // Mean intensity at the constructive port: ½(1 + e^{−π·Δν·Δτ}).
        const int nSamples = 100_000;
        const int d1 = 10;
        const int d2 = 210;
        double deltaTau = (d2 - d1) * DtSeconds;
        double expectedMean = 0.5 * (1 + Math.Exp(-Math.PI * LinewidthHz * deltaTau));

        var input = NoisyCwField(LinewidthHz, nSamples, seed: 2);
        var output = TimeDomainConvolver.Convolve(input, MziImpulseResponse(d1, d2));

        double mean = 0;
        int count = 0;
        for (int n = d2; n < nSamples; n++, count++)
            mean += output[n].Real * output[n].Real + output[n].Imaginary * output[n].Imaginary;
        mean /= count;

        mean.ShouldBe(expectedMean, 0.06);
        mean.ShouldBeLessThan(0.95, "finite Δν·Δτ must dynamically wash out the fringe");
    }

    [Fact]
    public void UnbalancedMzi_ZeroLinewidth_KeepsFullConstructiveOutput()
    {
        const int nSamples = 2000;
        var input = NoisyCwField(0, nSamples, seed: 3);
        var output = TimeDomainConvolver.Convolve(input, MziImpulseResponse(10, 210));

        for (int n = 210; n < nSamples; n++)
            (output[n].Real * output[n].Real + output[n].Imaginary * output[n].Imaginary)
                .ShouldBe(1.0, 1e-9);
    }

    [Fact]
    public void TwoIndependentLasers_BeatCarriesCombinedLinewidth()
    {
        // E[cos(φ1 − φ2)](T) = e^{−π·(Δν1+Δν2)·T}: the relative phase random-walks
        // with the SUM of the linewidths, not either one alone.
        const double linewidth1 = 1e9;
        const double linewidth2 = 1.5e9;
        const int nSamples = 65;
        const int trials = 3000;
        double time = (nSamples - 1) * DtSeconds;
        double expectedCombined = Math.Exp(-Math.PI * (linewidth1 + linewidth2) * time);
        double singleLaserDecay = Math.Exp(-Math.PI * linewidth1 * time);

        var pin1 = Guid.NewGuid();
        var pin2 = Guid.NewGuid();
        double sum = 0;
        for (int t = 0; t < trials; t++)
        {
            var settings = new PhaseNoiseSettings(seed: 100 + t);
            settings.AddSource(pin1, new PhaseNoiseSource(linewidth1, "laserA"));
            settings.AddSource(pin2, new PhaseNoiseSource(linewidth2, "laserB"));
            var factors = settings.BuildPhaseFactors(DtSeconds, nSamples);
            var relative = factors[pin1][nSamples - 1] * Complex.Conjugate(factors[pin2][nSamples - 1]);
            sum += relative.Real;
        }
        double measured = sum / trials;

        measured.ShouldBe(expectedCombined, 0.05);
        measured.ShouldBeLessThan(singleLaserDecay - 0.05,
            "the beat must decohere faster than either laser alone");
    }

    [Fact]
    public void PhaseLockedLasers_ShowNoBeatNoise()
    {
        // Locked = shared walk + static offset → intensity of the sum is constant.
        var pin1 = Guid.NewGuid();
        var pin2 = Guid.NewGuid();
        const int nSamples = 2000;
        var settings = new PhaseNoiseSettings();
        settings.AddSource(pin1, new PhaseNoiseSource(LinewidthHz, "master"));
        settings.AddSource(pin2, new PhaseNoiseSource(LinewidthHz, "master", Math.PI / 3));

        var factors = settings.BuildPhaseFactors(DtSeconds, nSamples);
        double expected = (factors[pin1][0] + factors[pin2][0]).Magnitude;
        for (int n = 0; n < nSamples; n++)
            (factors[pin1][n] + factors[pin2][n]).Magnitude.ShouldBe(expected, 1e-9);
    }
}
