using CAP_Core.Components.Core;

namespace CAP_Core.Analysis.LogicAnalysis;

/// <summary>
/// The collision a candidate signal name would cause in a logic network. Mirrors the
/// rejections <see cref="LogicNetworkBuilder"/> enforces at build time: two outputs
/// sharing one tap name (output names never merge) and a name spanning both an input
/// and an output (cross-role — one name must not read as two wires). Same-named
/// inputs merge by design and are never a collision.
/// </summary>
public enum SignalCollisionKind
{
    /// <summary>The candidate name is free — the build accepts it.</summary>
    None,

    /// <summary>Another gate output already resolves to the same tap name.</summary>
    DuplicateOutput,

    /// <summary>The name is used in the other role (an input's name on an output or vice versa).</summary>
    CrossRole,
}

/// <summary>
/// Read-only probe answering "would this signal name collide in the current design?"
/// from the gate groups' persisted <see cref="TruthTablePinAssignment"/>s — the same
/// source <see cref="LogicNetworkBuilder"/> validates, without triggering a build.
/// Naming follows the builder's exact rules: a signal-named pin takes its signal,
/// an unnamed pin keeps its raw <c>&lt;gate&gt;.&lt;pin&gt;</c> name. Groups without
/// a persisted assignment are not gates and contribute no names.
/// </summary>
public static class SignalNameCollisionProbe
{
    /// <summary>
    /// Classifies a candidate signal name for one pin against every gate group:
    /// an input candidate colliding with any output tap name (signal-named or raw)
    /// is a <see cref="SignalCollisionKind.CrossRole"/>, an output candidate colliding
    /// with another output's tap name is a <see cref="SignalCollisionKind.DuplicateOutput"/>,
    /// and an output candidate equal to an input's name is a cross-role collision too.
    /// Duplicate-output wins when both apply — the same-role conflict is the plainer one.
    /// </summary>
    /// <param name="gateGroups">The canvas's gate groups (persisted assignment carrying groups).</param>
    /// <param name="editedGroup">The group the edited pin belongs to.</param>
    /// <param name="pinName">The edited pin's name within <paramref name="editedGroup"/>.</param>
    /// <param name="isInput">True when the edited pin plays an input role, false for an output.</param>
    /// <param name="candidateName">The signal name being typed; whitespace-only or empty never collides.</param>
    /// <returns>What the candidate would collide with, or <see cref="SignalCollisionKind.None"/>.</returns>
    public static SignalCollisionKind Classify(
        IReadOnlyList<ComponentGroup> gateGroups,
        ComponentGroup editedGroup,
        string pinName,
        bool isInput,
        string candidateName)
    {
        var candidate = candidateName.Trim();
        if (candidate.Length == 0)
            return SignalCollisionKind.None;

        var outputTaps = new Dictionary<string, List<(ComponentGroup Group, string Pin)>>();
        var inputs = new HashSet<string>();
        foreach (var group in gateGroups)
            CollectNames(group, outputTaps, inputs);

        if (isInput)
            return outputTaps.ContainsKey(candidate)
                ? SignalCollisionKind.CrossRole
                : SignalCollisionKind.None;

        if (outputTaps.TryGetValue(candidate, out var taps)
            && taps.Any(tap => !ReferenceEquals(tap.Group, editedGroup) || tap.Pin != pinName))
            return SignalCollisionKind.DuplicateOutput;
        return inputs.Contains(candidate)
            ? SignalCollisionKind.CrossRole
            : SignalCollisionKind.None;
    }

    /// <summary>Registers one group's effective input and output tap names the way the builder derives them.</summary>
    private static void CollectNames(
        ComponentGroup group,
        IDictionary<string, List<(ComponentGroup Group, string Pin)>> outputTaps,
        ISet<string> inputs)
    {
        var assignment = group.TruthTablePinAssignment;
        if (assignment == null)
            return;
        foreach (var pin in assignment.InputPinNames)
            inputs.Add(SignalOrRaw(assignment.InputSignalNames, pin, group.GroupName));
        foreach (var pin in assignment.OutputPinNames)
        {
            var tapName = SignalOrRaw(assignment.OutputSignalNames, pin, group.GroupName);
            if (!outputTaps.TryGetValue(tapName, out var taps))
                outputTaps[tapName] = taps = new List<(ComponentGroup, string)>();
            taps.Add((group, pin));
        }
    }

    /// <summary>The pin's signal name when it carries one, else its raw <c>&lt;gate&gt;.&lt;pin&gt;</c> name.</summary>
    private static string SignalOrRaw(
        IReadOnlyDictionary<string, string>? signalNames, string pinName, string gateName) =>
        signalNames != null && signalNames.TryGetValue(pinName, out var signal)
            ? signal
            : $"{gateName}.{pinName}";
}
