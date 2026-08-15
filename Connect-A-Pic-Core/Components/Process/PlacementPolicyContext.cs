using System;
using System.Collections.Generic;
using System.Linq;
using CAP_Core.Components.Core;

namespace CAP_Core.Components.Process;

/// <summary>
/// Shared, live-evaluated context for the single-process placement policy (issues #570/#653/#737)
/// and its per-chiplet scoping (#935).
/// Built once by <c>MainViewModel</c> and handed as one reference to every placement consumer
/// (manual canvas interaction, AI grid service, …), replacing four per-consumer duplicated
/// nullable funcs whose wiring could silently diverge. Accessors evaluate lazily so the context
/// always reflects the current design state.
/// </summary>
public sealed class PlacementPolicyContext
{
    private readonly Func<ActiveProcessSelection?> _getActiveProcess;
    private readonly Func<IReadOnlyCollection<string>> _getProcessAgnosticPdkNames;
    private readonly Func<Component, string?> _resolveComponentPdkSource;
    private readonly Func<IReadOnlyCollection<string>?> _resolveLiveMemberPdkNames;
    private readonly Func<IReadOnlyList<ProcessGroup>> _getProcessCatalog;

    /// <summary>
    /// Creates a context from live accessors.
    /// </summary>
    /// <param name="getActiveProcess">Returns the design's active process, or null when unset.</param>
    /// <param name="getProcessAgnosticPdkNames">Returns the names of loaded process-agnostic tool PDKs.</param>
    /// <param name="resolveComponentPdkSource">Resolves a placed component's PDK source (null = built-in/unknown).</param>
    /// <param name="resolveLiveMemberPdkNames">Returns by-value-compatible member PDK names for
    /// the active process, computed live against the current PDK catalog; null falls back to the
    /// persisted snapshot.</param>
    /// <param name="getProcessCatalog">Returns the live process catalog over all loaded PDKs,
    /// used to derive chiplet process bindings (issue #935); null disables chiplet scoping
    /// and keeps the canvas-global behavior.</param>
    public PlacementPolicyContext(
        Func<ActiveProcessSelection?> getActiveProcess,
        Func<IReadOnlyCollection<string>> getProcessAgnosticPdkNames,
        Func<Component, string?> resolveComponentPdkSource,
        Func<IReadOnlyCollection<string>?>? resolveLiveMemberPdkNames = null,
        Func<IReadOnlyList<ProcessGroup>>? getProcessCatalog = null)
    {
        _getActiveProcess = getActiveProcess ?? throw new ArgumentNullException(nameof(getActiveProcess));
        _getProcessAgnosticPdkNames = getProcessAgnosticPdkNames ?? throw new ArgumentNullException(nameof(getProcessAgnosticPdkNames));
        _resolveComponentPdkSource = resolveComponentPdkSource ?? throw new ArgumentNullException(nameof(resolveComponentPdkSource));
        _resolveLiveMemberPdkNames = resolveLiveMemberPdkNames ?? (() => null);
        _getProcessCatalog = getProcessCatalog ?? (() => Array.Empty<ProcessGroup>());
    }

    /// <summary>
    /// Context that never restricts placement — the default before <c>MainViewModel</c> wires the
    /// real one, matching the previous "unwired func → allow" fallback (fresh/Playground designs).
    /// </summary>
    public static PlacementPolicyContext Unrestricted { get; } =
        new(() => null, () => Array.Empty<string>(), _ => null);

    /// <summary>The design's active process selection, or null when unset.</summary>
    public ActiveProcessSelection? ActiveProcess => _getActiveProcess();

    /// <summary>Names of loaded PDKs flagged process-agnostic (e.g. "Analysis Tools").</summary>
    public IReadOnlyCollection<string> ProcessAgnosticPdkNames => _getProcessAgnosticPdkNames();

    /// <summary>
    /// Resolves the PDK source of a placed core component from the loaded library
    /// (groups carry none of their own, so their children are resolved individually — issue #653).
    /// </summary>
    public string? ResolveComponentPdkSource(Component component) => _resolveComponentPdkSource(component);

    /// <summary>By-value-compatible member PDK names for the active process, computed live
    /// against the current PDK catalog (null = fall back to the persisted snapshot).</summary>
    public IReadOnlyCollection<string>? LiveMemberPdkNames => _resolveLiveMemberPdkNames();

    /// <summary>
    /// Checks a single component's placement against the current context.
    /// See <see cref="SingleProcessPolicy.CheckPlacement"/>.
    /// </summary>
    public (bool IsAllowed, string? BlockReason) CheckPlacement(string? componentPdkName) =>
        SingleProcessPolicy.CheckPlacement(ActiveProcess, componentPdkName, ProcessAgnosticPdkNames, LiveMemberPdkNames);

    /// <summary>
    /// Checks a group's placement (over its children's PDK sources) against the current context.
    /// See <see cref="GroupProcessPolicy.CheckGroupPlacement"/>.
    /// </summary>
    public (bool IsAllowed, string? BlockReason) CheckGroupPlacement(
        IEnumerable<string?> childPdkSources, string? groupName = null) =>
        GroupProcessPolicy.CheckGroupPlacement(ActiveProcess, childPdkSources, ProcessAgnosticPdkNames, LiveMemberPdkNames, groupName);

    /// <summary>The live process catalog over all loaded PDKs (empty when unwired).</summary>
    public IReadOnlyList<ProcessGroup> ProcessCatalog => _getProcessCatalog();

    /// <summary>
    /// The process scope a chiplet enforces on content placed into or onto it (issue #935):
    /// the group's explicit <see cref="ComponentGroup.ProcessBinding"/> when set, else the
    /// binding derived live from its children. Null = unbound group — the canvas-level
    /// process applies.
    /// </summary>
    public ActiveProcessSelection? ResolveChipletProcess(ComponentGroup group) =>
        group.ProcessBinding ?? GroupProcessPolicy.DeriveProcessBinding(
            ChildPdkSources(group), ProcessCatalog, ProcessAgnosticPdkNames);

    /// <summary>
    /// Checks a component placement against the process scope at the drop target: the target
    /// chiplet's process when placing into/onto a bound chiplet (issue #935), the
    /// canvas-level active process otherwise.
    /// </summary>
    public (bool IsAllowed, string? BlockReason) CheckPlacementAt(string? componentPdkName, ComponentGroup? targetGroup)
    {
        if (targetGroup != null && ResolveChipletProcess(targetGroup) is { } chipletProcess)
        {
            return SingleProcessPolicy.CheckPlacement(
                chipletProcess, componentPdkName, ProcessAgnosticPdkNames,
                chipletName: targetGroup.GroupName);
        }
        return CheckPlacement(componentPdkName);
    }

    /// <summary>
    /// Checks a group's placement at the drop target (issue #935). Onto a bound chiplet the
    /// chiplet's process is the scope. At canvas level a group that fails the design-wide
    /// check is still placeable as its own chiplet when all its children belong to exactly
    /// one catalog process; that binding comes back as <c>DerivedBinding</c> for the caller
    /// to pin onto the placed instance.
    /// </summary>
    public (bool IsAllowed, string? BlockReason, ActiveProcessSelection? DerivedBinding) CheckGroupPlacementAt(
        ComponentGroup group, ComponentGroup? targetGroup, string? groupName = null)
    {
        var childSources = ChildPdkSources(group).ToList();
        if (targetGroup != null && ResolveChipletProcess(targetGroup) is { } chipletProcess)
        {
            var chipletCheck = GroupProcessPolicy.CheckGroupPlacement(
                chipletProcess, childSources, ProcessAgnosticPdkNames,
                groupName: groupName, chipletName: targetGroup.GroupName);
            return chipletCheck.IsAllowed
                ? (true, null, group.ProcessBinding)
                : (false, chipletCheck.BlockReason, null);
        }

        var check = CheckGroupPlacement(childSources, groupName);
        if (check.IsAllowed)
        {
            return (true, null,
                group.ProcessBinding ?? GroupProcessPolicy.DeriveProcessBinding(
                    childSources, ProcessCatalog, ProcessAgnosticPdkNames));
        }

        var derived = GroupProcessPolicy.DeriveProcessBinding(childSources, ProcessCatalog, ProcessAgnosticPdkNames);
        return derived != null
            ? (true, null, derived)
            : (false, check.BlockReason, null);
    }

    /// <summary>
    /// Paste guard for one top-level clipboard entry (issues #653/#935): a loose component is
    /// checked against the canvas process; a copied group is additionally admitted as its own
    /// chiplet when its children uniformly belong to one catalog process.
    /// </summary>
    public bool IsPasteEntryAllowed(bool isGroupEntry, IReadOnlyList<string?> entryPdkSources)
    {
        if (GroupProcessPolicy.CheckGroupPlacement(
                ActiveProcess, entryPdkSources, ProcessAgnosticPdkNames, LiveMemberPdkNames).IsAllowed)
            return true;
        return isGroupEntry &&
            GroupProcessPolicy.DeriveProcessBinding(entryPdkSources, ProcessCatalog, ProcessAgnosticPdkNames) != null;
    }

    /// <summary>Resolved PDK source of every recursive non-group child of <paramref name="group"/>.</summary>
    private IEnumerable<string?> ChildPdkSources(ComponentGroup group) =>
        group.GetAllComponentsRecursive()
            .Where(child => child is not ComponentGroup)
            .Select(ResolveComponentPdkSource);
}
