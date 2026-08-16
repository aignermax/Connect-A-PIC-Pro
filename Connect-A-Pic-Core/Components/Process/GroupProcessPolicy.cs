using System;
using System.Collections.Generic;
using System.Linq;
using CAP_Core.Components.Core;

namespace CAP_Core.Components.Process;

/// <summary>
/// Extends <see cref="SingleProcessPolicy"/> to component groups (issue #653). A group carries
/// no <c>PdkSource</c> of its own, so its process membership is derived from its child
/// components' PDK sources: the group is placeable only when every child passes the
/// per-component placement check (member / built-in / process-agnostic). Since issue #935 a
/// group may additionally act as a chiplet with its own process scope — see
/// <see cref="DeriveProcessBinding"/> and <see cref="ComponentGroup.ProcessBinding"/>.
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
    /// parameter doc (issue #732). Non-null REPLACES the persisted snapshot as the membership
    /// authority; null falls back to the snapshot.
    /// </param>
    /// <param name="groupName">Display name of the group, used in the block message.</param>
    /// <param name="chipletName">
    /// Name of the target chiplet when the check runs against a chiplet's process scope
    /// (issue #935) instead of the canvas lock; changes the block message's wording only.
    /// </param>
    public static (bool IsAllowed, string? BlockReason) CheckGroupPlacement(
        ActiveProcessSelection? active,
        IEnumerable<string?> childPdkSources,
        IReadOnlyCollection<string>? processAgnosticPdkNames = null,
        IReadOnlyCollection<string>? liveMemberPdkNames = null,
        string? groupName = null,
        string? chipletName = null)
    {
        var blockedPdkNames = childPdkSources
            .Where(pdk => !SingleProcessPolicy.CheckPlacement(active, pdk, processAgnosticPdkNames, liveMemberPdkNames).IsAllowed)
            .Select(pdk => pdk!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (blockedPdkNames.Count == 0)
            return (true, null);

        var groupLabel = string.IsNullOrWhiteSpace(groupName) ? "This group" : $"Group '{groupName}'";
        if (!string.IsNullOrWhiteSpace(chipletName))
        {
            return (false,
                $"{groupLabel} contains component(s) from '{string.Join("', '", blockedPdkNames)}', but " +
                $"chiplet '{chipletName}' fabricates in the process '{active!.DisplayName}' — a chiplet " +
                "uses one process. Place the group outside the chiplet, or use Playground to mix processes.");
        }
        return (false,
            $"{groupLabel} contains component(s) from '{string.Join("', '", blockedPdkNames)}', but the " +
            $"chip is locked to the process '{active!.DisplayName}'. A monolithic design uses one process — " +
            "start a new design (or use Playground) to mix processes. A group whose components all " +
            "belong to one other process can be placed as its own chiplet.");
    }

    /// <summary>
    /// Derives a group's chiplet process binding (issue #935) from its children's PDK
    /// sources: when every non-exempt child belongs to exactly one process of the live
    /// <paramref name="processCatalog"/>, the group is a chiplet fabricated in that
    /// process. Returns null when the group carries no process content (built-in/tool
    /// children only) or its children span PDKs no single catalog process covers — such
    /// a group cannot be fabricated as one chiplet.
    /// </summary>
    /// <param name="childPdkSources">PDK source of every child component (null = built-in/unknown).</param>
    /// <param name="processCatalog">The live process catalog over all loaded PDKs.</param>
    /// <param name="processAgnosticPdkNames">Names of PDKs flagged process-agnostic (tool libraries).</param>
    public static ActiveProcessSelection? DeriveProcessBinding(
        IEnumerable<string?> childPdkSources,
        IReadOnlyList<ProcessGroup> processCatalog,
        IReadOnlyCollection<string>? processAgnosticPdkNames = null)
    {
        var sources = childPdkSources
            .Where(pdk => !SingleProcessPolicy.IsExempt(pdk, processAgnosticPdkNames))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (sources.Count == 0)
            return null;

        var coveringProcesses = processCatalog
            .Where(group => sources.All(source =>
                group.MemberPdkNames.Contains(source, StringComparer.OrdinalIgnoreCase)))
            .ToList();

        return coveringProcesses.Count == 1
            ? ActiveProcessSelection.ForGroup(coveringProcesses[0])
            : null;
    }
}
