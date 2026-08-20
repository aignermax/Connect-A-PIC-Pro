using System;
using System.Collections.Generic;
using System.Linq;

namespace CAP_Core.Components.Process;

/// <summary>
/// Enforces the single-process-per-design rule at component placement (issue #570).
/// Process-keyed successor to the PDK-name-based policy from PR #602.
/// </summary>
public static class SingleProcessPolicy
{
    /// <summary>The reserved PDK-source label for process-agnostic built-in/tool components.</summary>
    public const string BuiltInSource = "Built-in";

    /// <summary>True when the PDK source denotes a built-in / tool (process-agnostic) component.</summary>
    public static bool IsBuiltIn(string? pdkSource) =>
        string.IsNullOrWhiteSpace(pdkSource) ||
        string.Equals(pdkSource, BuiltInSource, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the PDK source is exempt from process enforcement: built-in components and
    /// members of process-agnostic tool PDKs (e.g. "Analysis Tools"). Single source of truth
    /// shared by placement checks and legacy-file migration so the two can never drift.
    /// </summary>
    public static bool IsExempt(string? pdkSource, IReadOnlyCollection<string>? processAgnosticPdkNames) =>
        IsBuiltIn(pdkSource) ||
        (processAgnosticPdkNames?.Contains(pdkSource!, StringComparer.OrdinalIgnoreCase) ?? false);

    /// <summary>
    /// Decides whether a component from <paramref name="componentPdkName"/> may be placed on a
    /// design locked to <paramref name="active"/>. Built-ins, process-agnostic PDKs (tool
    /// libraries such as "Analysis Tools" — see <paramref name="processAgnosticPdkNames"/>),
    /// Playground, and an unset selection always pass; otherwise the component's PDK must be an
    /// effective member of the active process: <paramref name="liveMemberPdkNames"/> when
    /// provided, else the persisted <paramref name="active"/> snapshot.
    /// </summary>
    /// <param name="active">The design's active process selection, or null when unset.</param>
    /// <param name="componentPdkName">PDK source of the component being placed.</param>
    /// <param name="processAgnosticPdkNames">Names of PDKs flagged process-agnostic (tool libraries).</param>
    /// <param name="liveMemberPdkNames">
    /// The by-value-compatible member PDK names for <paramref name="active"/>, recomputed against
    /// the live catalog (see <c>LeftPanelViewModel.ResolveLiveMemberPdkNames</c>, issue #732).
    /// When non-null it REPLACES the persisted <see cref="ActiveProcessSelection.MemberPdkNames"/>
    /// snapshot as the membership authority — not a union with it. Both directions matter: the
    /// snapshot is frozen when the process is saved with the design, so a value-compatible custom
    /// PDK registered afterward exists only in the live set (would otherwise stay locked out
    /// forever), and a snapshot member whose process was edited into incompatibility afterward is
    /// absent from the live set (a snapshot-OR-live union would keep it pasteable while the
    /// library filter correctly hides it). The live resolution itself already falls back to the
    /// snapshot when the active process has no computable fingerprint. Null (unwired caller)
    /// falls back to the snapshot, preserving prior behavior.
    /// </param>
    /// <param name="chipletName">
    /// Name of the target chiplet when the check runs against a chiplet's process scope
    /// (issue #935) instead of the canvas lock; changes the block message's wording only.
    /// </param>
    public static (bool IsAllowed, string? BlockReason) CheckPlacement(
        ActiveProcessSelection? active, string? componentPdkName,
        IReadOnlyCollection<string>? processAgnosticPdkNames = null,
        IReadOnlyCollection<string>? liveMemberPdkNames = null,
        string? chipletName = null)
    {
        if (IsExempt(componentPdkName, processAgnosticPdkNames))
            return (true, null);

        if (active == null || active.IsPlayground)
            return (true, null);

        var effectiveMembers = liveMemberPdkNames ?? (IEnumerable<string>)active.MemberPdkNames;
        if (effectiveMembers.Contains(componentPdkName!, StringComparer.OrdinalIgnoreCase))
            return (true, null);

        if (!string.IsNullOrWhiteSpace(chipletName))
        {
            return (false,
                $"This component belongs to '{componentPdkName}', but chiplet '{chipletName}' " +
                $"fabricates in the process '{active.DisplayName}' — a chiplet uses one process. " +
                "Place the component outside the chiplet, or use Playground to mix processes.");
        }

        return (false,
            $"This component belongs to '{componentPdkName}', but the chip is locked to the process " +
            $"'{active.DisplayName}'. A monolithic design uses one process — start a new design (or use " +
            "Playground) to mix processes. Content grouped as its own chiplet carries its own process.");
    }
}
