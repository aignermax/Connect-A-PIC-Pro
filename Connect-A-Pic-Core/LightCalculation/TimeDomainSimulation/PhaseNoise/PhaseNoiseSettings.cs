using System.Numerics;

namespace CAP_Core.LightCalculation.TimeDomainSimulation.PhaseNoise;

/// <summary>
/// Per-run laser phase-noise configuration for the transient engine (issue #834):
/// maps every active input pin to its <see cref="PhaseNoiseSource"/> and realises
/// one seeded Wiener phase walk per lock group. Multiplying each input envelope by
/// its unit phase factor e^{iφ(t)} lets the ordinary convolution with h(t) apply
/// every path's group delay to the phase process — unbalanced interferometers
/// dephase realistically while balanced paths cancel the common mode by construction.
/// </summary>
public class PhaseNoiseSettings
{
    /// <summary>Default RNG seed so repeated runs of the same design are reproducible.</summary>
    public const int DefaultSeed = 834;

    private const double SpeedOfLightMPerS = 2.998e8;
    private const double NanometersPerMeter = 1e9;

    private readonly Dictionary<Guid, PhaseNoiseSource> _sources = new();

    /// <summary>Creates settings with the given RNG seed.</summary>
    /// <param name="seed">Base seed; each lock group derives its own stream from it.</param>
    public PhaseNoiseSettings(int seed = DefaultSeed)
    {
        Seed = seed;
    }

    /// <summary>Base RNG seed for all phase walks of this run.</summary>
    public int Seed { get; }

    /// <summary>Configured sources keyed by input pin id.</summary>
    public IReadOnlyDictionary<Guid, PhaseNoiseSource> Sources => _sources;

    /// <summary>True when at least one source has a finite linewidth.</summary>
    public bool HasAnyNoise => _sources.Values.Any(s => s.LinewidthHz > 0);

    /// <summary>Registers (or replaces) the phase-noise source feeding an input pin.</summary>
    /// <param name="inputPinId">Inflow pin id used as key in the transient input signals.</param>
    /// <param name="source">Laser phase-noise description.</param>
    public void AddSource(Guid inputPinId, PhaseNoiseSource source)
        => _sources[inputPinId] = source ?? throw new ArgumentNullException(nameof(source));

    /// <summary>
    /// Realises the phase walks and returns the unit phase factor e^{i(φ(t)+offset)}
    /// per configured input pin. Pins in the same lock group share one walk; group
    /// seeds derive deterministically from <see cref="Seed"/> and the ordinal group order.
    /// </summary>
    /// <param name="dtSeconds">Simulation time step in seconds.</param>
    /// <param name="nSamples">Number of time samples.</param>
    public Dictionary<Guid, Complex[]> BuildPhaseFactors(double dtSeconds, int nSamples)
    {
        var groupPhases = BuildGroupPhases(dtSeconds, nSamples);
        var factors = new Dictionary<Guid, Complex[]>();
        foreach (var (pinId, source) in _sources)
        {
            var phases = groupPhases[source.LockGroupId];
            var pinFactors = new Complex[nSamples];
            for (int n = 0; n < nSamples; n++)
                pinFactors[n] = Complex.FromPolarCoordinates(1, phases[n] + source.PhaseOffsetRad);
            factors[pinId] = pinFactors;
        }
        return factors;
    }

    /// <summary>
    /// Converts a linewidth given as FWHM in nanometers into Hz via Δν = c·Δλ/λ².
    /// </summary>
    /// <param name="fwhmNm">Linewidth FWHM in nm.</param>
    /// <param name="centerWavelengthNm">Center wavelength in nm.</param>
    public static double LinewidthFwhmNmToHz(double fwhmNm, double centerWavelengthNm)
    {
        if (fwhmNm <= 0 || centerWavelengthNm <= 0)
            return 0;
        double lambdaM = centerWavelengthNm / NanometersPerMeter;
        double deltaLambdaM = fwhmNm / NanometersPerMeter;
        return SpeedOfLightMPerS * deltaLambdaM / (lambdaM * lambdaM);
    }

    /// <summary>One Wiener walk per lock group; groups are seeded in ordinal-sorted order.</summary>
    private Dictionary<string, double[]> BuildGroupPhases(double dtSeconds, int nSamples)
    {
        var groups = _sources.Values
            .GroupBy(s => s.LockGroupId, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        var groupPhases = new Dictionary<string, double[]>(StringComparer.Ordinal);
        for (int i = 0; i < groups.Count; i++)
        {
            double linewidthHz = groups[i].Max(s => s.LinewidthHz);
            var rng = new Random(unchecked(Seed * 31 + i));
            groupPhases[groups[i].Key] =
                WienerPhaseProcess.Generate(linewidthHz, dtSeconds, nSamples, rng);
        }
        return groupPhases;
    }
}
