using CAP_Core.Components.Core;
using CAP.Avalonia.ViewModels.Canvas;

namespace CAP.Avalonia.ViewModels.Analysis.AnalysisOutput;

/// <summary>Outcome classes of resolving the analysis output coupler (issue #754).</summary>
public enum AnalysisOutputState
{
    /// <summary>A coupler is designated, still exists, and its laser is off.</summary>
    DesignatedValid,

    /// <summary>A coupler is designated but its laser was switched back on.</summary>
    DesignatedLaserOn,

    /// <summary>The designated coupler was deleted; the designation has been cleared.</summary>
    DesignatedMissing,

    /// <summary>No designation; exactly one coupler has its laser off — use it automatically.</summary>
    AutoSingle,

    /// <summary>No designation and several couplers have their laser off — ambiguous.</summary>
    MultipleCandidates,

    /// <summary>Couplers exist but every laser is on (legacy design, no true output).</summary>
    AllLasersOn,

    /// <summary>The design contains no light-injecting coupler at all.</summary>
    NoCouplers,
}

/// <summary>
/// Result of <see cref="AnalysisOutputResolver.Resolve"/>: the classification, the
/// resolved output coupler (for <see cref="AnalysisOutputState.DesignatedValid"/>,
/// <see cref="AnalysisOutputState.DesignatedLaserOn"/> and
/// <see cref="AnalysisOutputState.AutoSingle"/>), and all off-laser candidates.
/// </summary>
/// <param name="State">Classification of the current output situation.</param>
/// <param name="Output">The resolved output coupler, when one is determined.</param>
/// <param name="Candidates">All couplers whose laser is off (picker candidates).</param>
public sealed record AnalysisOutputResolution(
    AnalysisOutputState State,
    ComponentViewModel? Output,
    IReadOnlyList<ComponentViewModel> Candidates);

/// <summary>
/// Resolves which coupler the Eye/BER and Transient analyses should evaluate,
/// combining the design-wide <see cref="AnalysisOutputDesignation"/> with the live
/// laser states (issue #754). Both analysis tabs call this so they can never disagree.
/// </summary>
public static class AnalysisOutputResolver
{
    /// <summary>
    /// Classifies the current output situation. A designation pointing at a deleted
    /// component is pruned here (cleared) so it cannot go stale, per the issue contract.
    /// </summary>
    /// <param name="canvas">Canvas providing components and the designation.</param>
    public static AnalysisOutputResolution Resolve(DesignCanvasViewModel canvas)
    {
        var couplers = canvas.Components.Where(c => c.IsLightSource).ToList();
        var offCandidates = couplers.Where(c => c.IsLaserOff).ToList();

        if (canvas.AnalysisOutput.CouplerId is Guid designatedId)
        {
            var designated = couplers.FirstOrDefault(c => c.Component.Id == designatedId);
            if (designated == null)
            {
                canvas.AnalysisOutput.Clear();
                return new(AnalysisOutputState.DesignatedMissing, null, offCandidates);
            }
            var state = designated.IsLaserOff
                ? AnalysisOutputState.DesignatedValid
                : AnalysisOutputState.DesignatedLaserOn;
            return new(state, designated, offCandidates);
        }

        if (couplers.Count == 0)
            return new(AnalysisOutputState.NoCouplers, null, offCandidates);

        return offCandidates.Count switch
        {
            0 => new(AnalysisOutputState.AllLasersOn, null, offCandidates),
            1 => new(AnalysisOutputState.AutoSingle, offCandidates[0], offCandidates),
            _ => new(AnalysisOutputState.MultipleCandidates, null, offCandidates),
        };
    }

    /// <summary>
    /// Collects the light-pin flow ids (both directions) of a coupler, matching how
    /// <c>TransientCircuitFactory.CollectOutputCouplerPinIds</c> keys traces, so the
    /// designated coupler's traces can be found regardless of flow direction.
    /// </summary>
    /// <param name="coupler">The coupler whose light pins to collect.</param>
    public static HashSet<Guid> CollectLightPinIds(ComponentViewModel coupler)
    {
        var pinIds = new HashSet<Guid>();
        foreach (var pin in coupler.Component.PhysicalPins)
        {
            if (pin.LogicalPin?.MatterType != MatterType.Light) continue;
            pinIds.Add(pin.LogicalPin.IDInFlow);
            pinIds.Add(pin.LogicalPin.IDOutFlow);
        }
        return pinIds;
    }
}
