using System.Numerics;

namespace CAP_Core.LightCalculation.TimeDomainSimulation.CompactModels.Models;

/// <summary>
/// Photodiode compact model: responsivity × incident optical power, low-pass
/// filtered by a first-order RC time constant.
///
/// i_target(t) = R · |E(t)|²  ;  τ · di/dt = i_target − i
///
/// The RC filter is discretized with the exact exponential step
/// i[n+1] = i[n] + (i_target − i[n]) · (1 − e^(−dt/τ)), which is
/// unconditionally stable for any dt. The photodiode absorbs the incident
/// light (outgoing field = 0) and reports the filtered photocurrent in A
/// as its electrical output. No electrical pin is required (issue #519):
/// the photocurrent is visible as a trace only.
/// </summary>
public class PhotodiodeRcModel : ICompactModel
{
    /// <summary>Registry name of this model.</summary>
    public const string ModelName = "PhotodiodeRc";

    /// <summary>Parameter key: responsivity in A/W.</summary>
    public const string ResponsivityKey = "responsivityAmpsPerWatt";

    /// <summary>Parameter key: RC time constant in seconds.</summary>
    public const string TimeConstantKey = "rcTimeConstantSeconds";

    /// <summary>Default responsivity in A/W (typical InGaAs pin diode at 1550 nm).</summary>
    public const double DefaultResponsivityAmpsPerWatt = 0.8;

    /// <summary>Default RC time constant in seconds (≈ 16 GHz bandwidth).</summary>
    public const double DefaultRcTimeConstantSeconds = 1e-11;

    private const string PhotocurrentStateKey = "photocurrentAmps";

    private readonly double _responsivity;
    private readonly double _timeConstant;

    /// <summary>Responsivity in A/W.</summary>
    public double ResponsivityAmpsPerWatt => _responsivity;

    /// <summary>RC time constant in seconds.</summary>
    public double RcTimeConstantSeconds => _timeConstant;

    /// <summary>Initializes a new instance of <see cref="PhotodiodeRcModel"/>.</summary>
    /// <param name="parameters">
    /// Optional model parameters (<see cref="ResponsivityKey"/>,
    /// <see cref="TimeConstantKey"/>); missing keys use the defaults.
    /// </param>
    public PhotodiodeRcModel(IReadOnlyDictionary<string, double>? parameters = null)
    {
        _responsivity = GetParameter(parameters, ResponsivityKey, DefaultResponsivityAmpsPerWatt);
        _timeConstant = GetParameter(parameters, TimeConstantKey, DefaultRcTimeConstantSeconds);

        if (_responsivity < 0)
            throw new ArgumentOutOfRangeException(nameof(parameters), "Responsivity must be ≥ 0.");
        if (_timeConstant <= 0)
            throw new ArgumentOutOfRangeException(nameof(parameters), "RC time constant must be > 0.");
    }

    /// <inheritdoc/>
    public string Name => ModelName;

    /// <inheritdoc/>
    public CompactModelState CreateInitialState() => new();

    /// <inheritdoc/>
    public CompactModelStepResult Step(
        double dt, Complex incidentField, CompactModelState state, double electricalInput)
    {
        double incidentPowerWatts =
            incidentField.Real * incidentField.Real +
            incidentField.Imaginary * incidentField.Imaginary;

        double targetCurrent = _responsivity * incidentPowerWatts;
        double current = state.Get(PhotocurrentStateKey);

        // Exact solution of the first-order ODE over one step.
        double alpha = 1.0 - Math.Exp(-dt / _timeConstant);
        current += (targetCurrent - current) * alpha;
        state.Set(PhotocurrentStateKey, current);

        // The photodiode absorbs the light — no outgoing optical field.
        return new CompactModelStepResult(Complex.Zero, current);
    }

    private static double GetParameter(
        IReadOnlyDictionary<string, double>? parameters, string key, double defaultValue)
        => parameters != null && parameters.TryGetValue(key, out var v) ? v : defaultValue;
}
