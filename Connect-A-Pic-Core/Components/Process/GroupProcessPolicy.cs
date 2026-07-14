using System;
using System.Collections.Generic;
using System.Linq;

namespace CAP_Core.Components.Process;

/// <summary>
/// Extends <see cref="SingleProcessPolicy"/> to component groups (issue #653). A group carries
/// no <c>PdkSource</c> of its own, so its process membership is derived from its child
/// components' PDK sources: the group is placeable only when every child passes the
/// per-component placement check (member / built-in / process-agnostic).
/// </summary>
public static class GroupProcessPolicy
{
    /// <summary>
    /// Decides whether a group whose (recursive, non-group) children come from
    /// <paramref name="childPdkSources"/> may be placed on a design locked to
    /// <paramref name="active"/>. Each child is evaluated with
    /// <see cref="SingleProcessPolicy.CheckPlacement"/>; a single foreign-process child
    /// blocks the whole group. Playground / unset selections always pass.
    /// </summary>
    /// <param name="active">The design's active process selection, or null when unset.</param>
    /// <param name="childPdkSources">PDK source of every child component (null = built-in/unknown).</param>
    /// <param name="processAgnosticPdkNames">Names of PDKs flagged process-agnostic (tool libraries).</param>
    /// <param name="liveMemberPdkNames">
    /// By-value-compatible member PDK names for <paramref name="active"/>, forwarded verbatim to
    /// <see cref="SingleProcessPolicy.CheckPlacement"/> for each child — see that method's
    /// parameter doc (issue #732). Null falls back to the persisted snapshot only.
    /// </param>
    /// <param name="groupName">Display name of the group, used in the block message.</param>
    public static (bool IsAllowed, string? BlockReason) CheckGroupPlacement(
        ActiveProcessSelection? active,
        IEnumerable<string?> childPdkSources,
        IReadOnlyCollection<string>? processAgnosticPdkNames = null,
        IReadOnlyCollection<string>? liveMemberPdkNames = null,
        string? groupName = null)
    {
        var blockedPdkNames = childPdkSources
            .Where(pdk => !SingleProcessPolicy.CheckPlacement(active, pdk, processAgnosticPdkNames, liveMemberPdkNames).IsAllowed)
            .Select(pdk => pdk!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (blockedPdkNames.Count == 0)
            return (true, null);

        var groupLabel = string.IsNullOrWhiteSpace(groupName) ? "This group" : $"Group '{groupName}'";
        return (false,
            $"{groupLabel} contains component(s) from '{string.Join("', '", blockedPdkNames)}', but the " +
            $"chip is locked to the process '{active!.DisplayName}'. A monolithic design uses one process — " +
            "start a new design (or use Playground) to mix processes.");
    }
}
