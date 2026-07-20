namespace CAP_Core.LightCalculation;

/// <summary>
/// Thrown when the multi-hop (Neumann-series) closure of a circuit's S-matrix cannot
/// produce a physically trustworthy result: the series diverges or fails to converge
/// within the iteration cap (resonant feedback loop, round-trip gain ≥ 1), or the
/// converged closure contains a transfer with |H| &gt; 1 (a passive circuit cannot
/// amplify light). Callers must abort the analysis and surface this message instead
/// of showing a truncated/clamped result — never fabricate physics (field round 4).
/// </summary>
public class NonConvergentCircuitException : InvalidOperationException
{
    /// <summary>Initializes the exception with a user-facing explanation.</summary>
    /// <param name="message">Explanation of why the closure was rejected.</param>
    public NonConvergentCircuitException(string message) : base(message)
    {
    }
}
