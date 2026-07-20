namespace CAP_Core.LightCalculation;

/// <summary>Why the multi-hop closure rejected the circuit (field round 4).</summary>
public enum NonConvergentCircuitKind
{
    /// <summary>
    /// (I − M) is singular or numerically indistinguishable from singular: a lossless
    /// feedback loop sits exactly on resonance, so the circuit has no steady state.
    /// </summary>
    ResonantLoop,

    /// <summary>
    /// The solved closure contains a transfer between externally observable pins with
    /// |H| &gt; 1 — a passive circuit cannot output more light than was injected.
    /// </summary>
    EnergyFabricated,

    /// <summary>
    /// A single component's S-matrix block already violates passivity (largest singular
    /// value &gt; 1): its S-parameter data or interpolation fabricates energy.
    /// </summary>
    NonPassiveComponent,
}

/// <summary>
/// Thrown when the multi-hop closure of a circuit's S-matrix cannot produce a physically
/// trustworthy result: the linear system (I − M)·X = B is singular (lossless feedback
/// loop exactly on resonance), the solved closure fabricates energy between externally
/// observable pins (|H| &gt; 1), or a component's S-matrix data is non-passive to begin
/// with. Callers must abort the analysis and surface this message instead of showing a
/// clamped/fabricated result — never fabricate physics (field round 4).
/// The structured properties let the UI layer render a fully localized message.
/// </summary>
public class NonConvergentCircuitException : InvalidOperationException
{
    /// <summary>Classification of the failure, for localized UI messages.</summary>
    public NonConvergentCircuitKind Kind { get; }

    /// <summary>
    /// Component names along one feedback loop of the circuit (for
    /// <see cref="NonConvergentCircuitKind.ResonantLoop"/>), or null when unknown.
    /// </summary>
    public IReadOnlyList<string>? LoopComponentNames { get; }

    /// <summary>The offending component (for <see cref="NonConvergentCircuitKind.NonPassiveComponent"/>).</summary>
    public string? ComponentName { get; }

    /// <summary>Wavelength the closure was computed for, when known.</summary>
    public int? WavelengthNm { get; }

    /// <summary>
    /// Energy excess in percent: (|H| − 1) · 100 for fabricated transfers, or
    /// (σ_max − 1) · 100 for a non-passive component block.
    /// </summary>
    public double? ExcessPercent { get; }

    /// <summary>Initializes the exception with a user-facing English explanation.</summary>
    /// <param name="message">Explanation of why the closure was rejected.</param>
    /// <param name="kind">Failure classification (default: resonant loop).</param>
    public NonConvergentCircuitException(
        string message,
        NonConvergentCircuitKind kind = NonConvergentCircuitKind.ResonantLoop) : base(message)
    {
        Kind = kind;
    }

    /// <summary>Initializes the exception with full structured diagnostics.</summary>
    /// <param name="message">Explanation of why the closure was rejected.</param>
    /// <param name="kind">Failure classification.</param>
    /// <param name="loopComponentNames">Component names along one feedback loop.</param>
    /// <param name="componentName">The offending component, when a single one is known.</param>
    /// <param name="wavelengthNm">Wavelength the closure was computed for.</param>
    /// <param name="excessPercent">Energy excess in percent above the passive limit.</param>
    public NonConvergentCircuitException(
        string message,
        NonConvergentCircuitKind kind,
        IReadOnlyList<string>? loopComponentNames = null,
        string? componentName = null,
        int? wavelengthNm = null,
        double? excessPercent = null) : base(message)
    {
        Kind = kind;
        LoopComponentNames = loopComponentNames;
        ComponentName = componentName;
        WavelengthNm = wavelengthNm;
        ExcessPercent = excessPercent;
    }
}
