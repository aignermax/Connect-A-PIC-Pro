using System.Numerics;

namespace CAP_Core.LightCalculation.TimeDomainSimulation.CompactModels.Models;

/// <summary>
/// Electro-optic phase modulator compact model: the drive voltage V(t) shifts
/// the optical phase linearly,
///
///   φ(t) = π · V(t) / V_π
///
/// The outgoing field is the incident field attenuated by the insertion loss
/// and rotated by φ(t). The model is memoryless (no integrator state) — the
/// electro-optic response is treated as instantaneous relative to the
/// simulation timestep. Electrical output = applied phase in rad (for tracing).
/// </summary>
public class ElectroOpticPhaseModulatorModel : ICompactModel
{
    /// <summary>Registry name of this model.</summary>
    public const string ModelName = "ElectroOpticPhaseModulator";

    /// <summary>Parameter key: half-wave voltage V_π in V.</summary>
    public const string VPiKey = "vPiVolts";

    /// <summary>Parameter key: insertion loss in dB (≥ 0).</summary>
    public const string InsertionLossKey = "insertionLossDb";

    /// <summary>Default half-wave voltage (typical LiNbO₃/Si modulator).</summary>
    public const double DefaultVPiVolts = 3.0;

    /// <summary>Default insertion loss in dB.</summary>
    public const double DefaultInsertionLossDb = 0.0;

    /// <summary>dB per amplitude decade factor: amplitude = 10^(−dB/20).</summary>
    private const double DbToAmplitudeDivisor = 20.0;

    private readonly double _vPi;
    private readonly double _amplitudeFactor;

    /// <summary>Half-wave voltage V_π in V.</summary>
    public double VPiVolts => _vPi;

    /// <summary>Initializes a new instance of <see cref="ElectroOpticPhaseModulatorModel"/>.</summary>
    /// <param name="parameters">
    /// Optional model parameters (<see cref="VPiKey"/>, <see cref="InsertionLossKey"/>);
    /// missing keys use the defaults.
    /// </param>
    public ElectroOpticPhaseModulatorModel(IReadOnlyDictionary<string, double>? parameters = null)
    {
        _vPi = GetParameter(parameters, VPiKey, DefaultVPiVolts);
        double lossDb = GetParameter(parameters, InsertionLossKey, DefaultInsertionLossDb);

        if (_vPi <= 0)
            throw new ArgumentOutOfRangeException(nameof(parameters), "V_π must be > 0.");
        if (lossDb < 0)
            throw new ArgumentOutOfRangeException(nameof(parameters), "Insertion loss must be ≥ 0 dB.");

        _amplitudeFactor = Math.Pow(10.0, -lossDb / DbToAmplitudeDivisor);
    }

    /// <inheritdoc/>
    public string Name => ModelName;

    /// <inheritdoc/>
    public CompactModelState CreateInitialState() => new();

    /// <inheritdoc/>
    public CompactModelStepResult Step(
        double dt, Complex incidentField, CompactModelState state, double electricalInput)
    {
        double phaseRadians = Math.PI * electricalInput / _vPi;
        Complex outgoing = incidentField * _amplitudeFactor
            * Complex.FromPolarCoordinates(1.0, phaseRadians);
        return new CompactModelStepResult(outgoing, phaseRadians);
    }

    private static double GetParameter(
        IReadOnlyDictionary<string, double>? parameters, string key, double defaultValue)
        => parameters != null && parameters.TryGetValue(key, out var v) ? v : defaultValue;
}
