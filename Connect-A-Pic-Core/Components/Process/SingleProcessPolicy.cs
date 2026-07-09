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
    /// Playground, and an unset selection always pass; otherwise the component's PDK must be a
    /// member of the active process.
    /// </summary>
    public static (bool IsAllowed, string? BlockReason) CheckPlacement(
        ActiveProcessSelection? active, string? componentPdkName,
        IReadOnlyCollection<string>? processAgnosticPdkNames = null)
    {
        if (IsExempt(componentPdkName, processAgnosticPdkNames))
            return (true, null);

        if (active == null || active.IsPlayground)
            return (true, null);

        if (active.MemberPdkNames.Contains(componentPdkName!, StringComparer.OrdinalIgnoreCase))
            return (true, null);

        return (false,
            $"This component belongs to '{componentPdkName}', but the chip is locked to the process " +
            $"'{active.DisplayName}'. A monolithic design uses one process — start a new design (or use " +
            "Playground) to mix processes.");
    }
}
