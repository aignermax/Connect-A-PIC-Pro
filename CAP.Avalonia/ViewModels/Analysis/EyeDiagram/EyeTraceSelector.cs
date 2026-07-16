using CAP_Core.LightCalculation.TimeDomainSimulation;

namespace CAP.Avalonia.ViewModels.Analysis.EyeDiagram;

/// <summary>
/// Picks the trace the eye/BER analysis should evaluate (#690). A coupler with
/// its laser switched off is the design's output, so its trace is preferred.
/// When every coupler is still emitting (legacy design) the strongest trace is
/// used as a fallback, with a warning that the choice is arbitrary.
/// </summary>
internal static class EyeTraceSelector
{
    /// <summary>Warning shown when no coupler has its laser switched off.</summary>
    public const string AllLasersOnWarning =
        "All couplers are emitting — turn the laser off on your output coupler(s) for a meaningful eye.";

    /// <summary>Error shown when the designated output coupler(s) receive no light.</summary>
    public const string NoSignalAtOutputError =
        "No light arrives at the coupler(s) with the laser switched off — check the path from your input coupler.";

    /// <summary>
    /// Outcome of the selection: either a <see cref="Trace"/> (possibly with a
    /// non-fatal <see cref="Warning"/>) or a user-facing <see cref="Error"/>.
    /// </summary>
    public sealed record Selection(double[]? Trace, string? Warning, string? Error);

    /// <summary>
    /// Selects the analysis trace from a completed transient result.
    /// </summary>
    /// <param name="result">Transient result with at least one pin trace.</param>
    /// <param name="outputCouplerPinIds">
    /// Flow ids of the light pins on couplers whose laser is off (true outputs).
    /// Empty when every laser is on.
    /// </param>
    public static Selection Select(TimeDomainResult result, IReadOnlyCollection<Guid> outputCouplerPinIds)
    {
        if (outputCouplerPinIds.Count == 0)
            return new Selection(SelectStrongest(result.PinTraces.Values), AllLasersOnWarning, null);

        var candidates = result.PinTraces
            .Where(kv => outputCouplerPinIds.Contains(kv.Key))
            .Select(kv => kv.Value)
            .ToList();

        return candidates.Count == 0
            ? new Selection(null, null, NoSignalAtOutputError)
            : new Selection(SelectStrongest(candidates), null, null);
    }

    /// <summary>Picks the highest-peak trace. The caller guarantees at least one exists.</summary>
    private static double[] SelectStrongest(IEnumerable<double[]> traces) =>
        traces.OrderByDescending(t => t.Length == 0 ? 0 : t.Max()).First();
}
