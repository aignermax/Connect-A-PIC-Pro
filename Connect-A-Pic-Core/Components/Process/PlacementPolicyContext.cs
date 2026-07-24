using System;
using System.Collections.Generic;
using CAP_Core.Components.Core;

namespace CAP_Core.Components.Process;

/// <summary>
/// Shared, live-evaluated context for the single-process placement policy (issues #570/#653/#737).
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

    /// <summary>
    /// Creates a context from live accessors.
    /// </summary>
    /// <param name="getActiveProcess">Returns the design's active process, or null when unset.</param>
    /// <param name="getProcessAgnosticPdkNames">Returns the names of loaded process-agnostic tool PDKs.</param>
    /// <param name="resolveComponentPdkSource">Resolves a placed component's PDK source (null = built-in/unknown).</param>
    /// <param name="resolveLiveMemberPdkNames">Returns by-value-compatible member PDK names for
    /// the active process, computed live against the current PDK catalog; null falls back to the
    /// persisted snapshot.</param>
    public PlacementPolicyContext(
        Func<ActiveProcessSelection?> getActiveProcess,
        Func<IReadOnlyCollection<string>> getProcessAgnosticPdkNames,
        Func<Component, string?> resolveComponentPdkSource,
        Func<IReadOnlyCollection<string>?>? resolveLiveMemberPdkNames = null)
    {
        _getActiveProcess = getActiveProcess ?? throw new ArgumentNullException(nameof(getActiveProcess));
        _getProcessAgnosticPdkNames = getProcessAgnosticPdkNames ?? throw new ArgumentNullException(nameof(getProcessAgnosticPdkNames));
        _resolveComponentPdkSource = resolveComponentPdkSource ?? throw new ArgumentNullException(nameof(resolveComponentPdkSource));
        _resolveLiveMemberPdkNames = resolveLiveMemberPdkNames ?? (() => null);
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
}
