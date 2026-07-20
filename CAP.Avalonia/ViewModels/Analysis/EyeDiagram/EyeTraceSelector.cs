using CAP_Core.LightCalculation.TimeDomainSimulation;
using CAP.Avalonia.Services.Localization;

namespace CAP.Avalonia.ViewModels.Analysis.EyeDiagram;

/// <summary>
/// Picks the trace the eye/BER analysis should evaluate (#690, #754). A user-designated
/// output coupler always wins; otherwise a coupler with its laser switched off is the
/// design's output. When several off couplers compete the strongest trace is used with
/// an explicit warning to pick one; when every coupler is still emitting (legacy design)
/// the strongest trace is used as a fallback, warning that the choice is arbitrary.
/// </summary>
internal static class EyeTraceSelector
{
    /// <summary>Warning shown when no coupler has its laser switched off.</summary>
    public const string AllLasersOnWarning =
        "All couplers are emitting — turn the laser off on your output coupler(s) for a meaningful eye.";

    /// <summary>Error shown when the coupler(s) with the laser off receive no light.</summary>
    public const string NoSignalAtOutputError =
        "No light from the switched-on input(s) arrives at the coupler(s) with the laser switched off — check the connection path from your input coupler.";

    /// <summary>Error shown when the user-designated output coupler receives no light (#754).</summary>
    public const string NoSignalAtDesignatedOutputError =
        "No light from the switched-on input(s) arrives at the designated output coupler — check the connection path from your input coupler to it.";

    /// <summary>
    /// Warning shown when several off couplers compete and none is designated (#754):
    /// the strongest trace is evaluated, but the user should pick the output explicitly.
    /// </summary>
    public static string MultipleOutputsWarning =>
        LocalizationService.Instance.Translate("Analysis.Output.MultipleCandidatesWarning");

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
    /// <param name="designatedPinIds">
    /// Flow ids of the light pins on the user-designated output coupler (#754), or
    /// null when no coupler is designated. When set, only these pins are evaluated.
    /// </param>
    /// <param name="hasMultipleCandidates">
    /// True when several off couplers compete without a designation, so the fallback
    /// choice gets an explicit warning instead of guessing silently (#754).
    /// </param>
    public static Selection Select(
        TimeDomainResult result,
        IReadOnlyCollection<Guid> outputCouplerPinIds,
        IReadOnlyCollection<Guid>? designatedPinIds = null,
        bool hasMultipleCandidates = false)
    {
        if (designatedPinIds != null)
        {
            var designated = TracesFor(result, designatedPinIds);
            return designated.Count == 0
                ? new Selection(null, null, NoSignalAtDesignatedOutputError)
                : new Selection(SelectStrongest(designated), null, null);
        }

        if (outputCouplerPinIds.Count == 0)
            return new Selection(SelectStrongest(result.PinTraces.Values), AllLasersOnWarning, null);

        var candidates = TracesFor(result, outputCouplerPinIds);
        if (candidates.Count == 0)
            return new Selection(null, null, NoSignalAtOutputError);

        var warning = hasMultipleCandidates ? MultipleOutputsWarning : null;
        return new Selection(SelectStrongest(candidates), warning, null);
    }

    /// <summary>Traces of the result whose pin id is in the given set.</summary>
    private static List<double[]> TracesFor(TimeDomainResult result, IReadOnlyCollection<Guid> pinIds) =>
        result.PinTraces
            .Where(kv => pinIds.Contains(kv.Key))
            .Select(kv => kv.Value)
            .ToList();

    /// <summary>Picks the highest-peak trace. The caller guarantees at least one exists.</summary>
    private static double[] SelectStrongest(IEnumerable<double[]> traces) =>
        traces.OrderByDescending(t => t.Length == 0 ? 0 : t.Max()).First();
}
