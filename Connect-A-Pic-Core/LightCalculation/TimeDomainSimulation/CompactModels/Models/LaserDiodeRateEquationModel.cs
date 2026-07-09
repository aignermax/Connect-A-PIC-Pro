using System.Numerics;

namespace CAP_Core.LightCalculation.TimeDomainSimulation.CompactModels.Models;

/// <summary>
/// Single-mode laser diode compact model based on the classic carrier/photon
/// rate equations (no Langevin noise — Phase 1 is deterministic):
///
///   dN/dt = I/(qV) − N/τn − g·(N − N₀)·S
///   dS/dt = g·(N − N₀)·S − S/τp + β·N/τn
///
/// N = carrier density (m⁻³), S = photon density (m⁻³), I = drive current (A).
/// Integrated with fixed-step RK4; the sample interval dt is internally split
/// into substeps ≤ τp/10 so the stiff photon dynamics stay stable even when
/// the simulation timestep is coarse. Output optical power is
/// P = κ·S with κ = <see cref="PhotonToPowerWattsKey"/>; the outgoing field is
/// √P (real, zero phase — Phase 1). Electrical output = P in W.
/// </summary>
public class LaserDiodeRateEquationModel : ICompactModel
{
    /// <summary>Registry name of this model.</summary>
    public const string ModelName = "LaserDiodeRateEquation";

    /// <summary>Parameter key: carrier lifetime τn in s.</summary>
    public const string CarrierLifetimeKey = "carrierLifetimeSeconds";

    /// <summary>Parameter key: photon lifetime τp in s.</summary>
    public const string PhotonLifetimeKey = "photonLifetimeSeconds";

    /// <summary>Parameter key: differential gain coefficient g in m³/s.</summary>
    public const string GainCoefficientKey = "gainCoefficientCubicMetersPerSecond";

    /// <summary>Parameter key: transparency carrier density N₀ in m⁻³.</summary>
    public const string TransparencyDensityKey = "transparencyDensityPerCubicMeter";

    /// <summary>Parameter key: spontaneous emission factor β (dimensionless).</summary>
    public const string SpontaneousEmissionFactorKey = "spontaneousEmissionFactor";

    /// <summary>Parameter key: active region volume V in m³.</summary>
    public const string ActiveVolumeKey = "activeVolumeCubicMeters";

    /// <summary>Parameter key: photon-density → output-power coefficient κ in W·m³.</summary>
    public const string PhotonToPowerWattsKey = "photonToPowerWattsPerDensity";

    /// <summary>Default carrier lifetime (1 ns, typical InGaAsP).</summary>
    public const double DefaultCarrierLifetimeSeconds = 1e-9;

    /// <summary>Default photon lifetime (3 ps).</summary>
    public const double DefaultPhotonLifetimeSeconds = 3e-12;

    /// <summary>Default gain coefficient (3·10⁻¹² m³/s).</summary>
    public const double DefaultGainCoefficient = 3e-12;

    /// <summary>Default transparency density (10²⁴ m⁻³).</summary>
    public const double DefaultTransparencyDensity = 1e24;

    /// <summary>Default spontaneous emission factor (10⁻⁴).</summary>
    public const double DefaultSpontaneousEmissionFactor = 1e-4;

    /// <summary>Default active volume (10⁻¹⁶ m³).</summary>
    public const double DefaultActiveVolumeCubicMeters = 1e-16;

    /// <summary>
    /// Default κ ≈ hν·V/(2τp) at 1550 nm for the default volume/lifetime,
    /// mapping photon density to emitted optical power (one facet).
    /// </summary>
    public const double DefaultPhotonToPowerWattsPerDensity = 2.1e-24;

    /// <summary>Elementary charge in C.</summary>
    public const double ElementaryChargeCoulombs = 1.602176634e-19;

    /// <summary>Internal RK4 substep ceiling relative to τp (dtSub ≤ τp / this).</summary>
    private const double SubstepsPerPhotonLifetime = 10.0;

    /// <summary>Guard against pathological dt/τp ratios that would hang the run.</summary>
    private const int MaxSubstepsPerSample = 100_000;

    private const string CarrierDensityStateKey = "carrierDensityPerCubicMeter";
    private const string PhotonDensityStateKey = "photonDensityPerCubicMeter";

    private readonly double _tauN;
    private readonly double _tauP;
    private readonly double _gain;
    private readonly double _n0;
    private readonly double _beta;
    private readonly double _volume;
    private readonly double _kappa;

    /// <summary>Initializes a new instance of <see cref="LaserDiodeRateEquationModel"/>.</summary>
    /// <param name="parameters">Optional model parameters; missing keys use the defaults.</param>
    public LaserDiodeRateEquationModel(IReadOnlyDictionary<string, double>? parameters = null)
    {
        _tauN = GetParameter(parameters, CarrierLifetimeKey, DefaultCarrierLifetimeSeconds);
        _tauP = GetParameter(parameters, PhotonLifetimeKey, DefaultPhotonLifetimeSeconds);
        _gain = GetParameter(parameters, GainCoefficientKey, DefaultGainCoefficient);
        _n0 = GetParameter(parameters, TransparencyDensityKey, DefaultTransparencyDensity);
        _beta = GetParameter(parameters, SpontaneousEmissionFactorKey, DefaultSpontaneousEmissionFactor);
        _volume = GetParameter(parameters, ActiveVolumeKey, DefaultActiveVolumeCubicMeters);
        _kappa = GetParameter(parameters, PhotonToPowerWattsKey, DefaultPhotonToPowerWattsPerDensity);

        if (_tauN <= 0 || _tauP <= 0 || _gain <= 0 || _volume <= 0 || _kappa <= 0)
            throw new ArgumentOutOfRangeException(nameof(parameters),
                "Laser lifetimes, gain, volume and power coefficient must be > 0.");
    }

    /// <inheritdoc/>
    public string Name => ModelName;

    /// <summary>
    /// Threshold current I_th = q·V·N_th/τn with N_th = N₀ + 1/(g·τp), in A.
    /// Useful for choosing physically sensible drive currents.
    /// </summary>
    public double ThresholdCurrentAmps
    {
        get
        {
            double nTh = _n0 + 1.0 / (_gain * _tauP);
            return ElementaryChargeCoulombs * _volume * nTh / _tauN;
        }
    }

    /// <inheritdoc/>
    public CompactModelState CreateInitialState() => new();

    /// <inheritdoc/>
    public CompactModelStepResult Step(
        double dt, Complex incidentField, CompactModelState state, double electricalInput)
    {
        double n = state.Get(CarrierDensityStateKey);
        double s = state.Get(PhotonDensityStateKey);
        double pumpRate = electricalInput / (ElementaryChargeCoulombs * _volume);

        int substeps = ComputeSubstepCount(dt);
        double dtSub = dt / substeps;
        for (int i = 0; i < substeps; i++)
            (n, s) = Rk4Step(n, s, pumpRate, dtSub);

        state.Set(CarrierDensityStateKey, n);
        state.Set(PhotonDensityStateKey, s);

        double powerWatts = _kappa * s;
        return new CompactModelStepResult(new Complex(Math.Sqrt(powerWatts), 0), powerWatts);
    }

    private int ComputeSubstepCount(double dt)
    {
        int substeps = Math.Max(1, (int)Math.Ceiling(dt * SubstepsPerPhotonLifetime / _tauP));
        if (substeps > MaxSubstepsPerSample)
            throw new InvalidOperationException(
                $"Laser rate-equation integration needs {substeps} substeps per sample " +
                $"(dt = {dt:E2} s, τp = {_tauP:E2} s). Reduce the simulation timestep.");
        return substeps;
    }

    private (double n, double s) Rk4Step(double n, double s, double pumpRate, double dt)
    {
        var (k1N, k1S) = Derivatives(n, s, pumpRate);
        var (k2N, k2S) = Derivatives(n + 0.5 * dt * k1N, s + 0.5 * dt * k1S, pumpRate);
        var (k3N, k3S) = Derivatives(n + 0.5 * dt * k2N, s + 0.5 * dt * k2S, pumpRate);
        var (k4N, k4S) = Derivatives(n + dt * k3N, s + dt * k3S, pumpRate);

        const double RkWeight = 1.0 / 6.0;
        n += dt * RkWeight * (k1N + 2 * k2N + 2 * k3N + k4N);
        s += dt * RkWeight * (k1S + 2 * k2S + 2 * k3S + k4S);

        // Densities are physically non-negative; clamp integration undershoot.
        return (Math.Max(n, 0), Math.Max(s, 0));
    }

    private (double dN, double dS) Derivatives(double n, double s, double pumpRate)
    {
        double stimulated = _gain * (n - _n0) * s;
        double dN = pumpRate - n / _tauN - stimulated;
        double dS = stimulated - s / _tauP + _beta * n / _tauN;
        return (dN, dS);
    }

    private static double GetParameter(
        IReadOnlyDictionary<string, double>? parameters, string key, double defaultValue)
        => parameters != null && parameters.TryGetValue(key, out var v) ? v : defaultValue;
}
