using CAP_Core.Components.Core;

namespace CAP_Core.Analysis.LogicAnalysis;

/// <summary>
/// Structural validation half of <see cref="LogicNetworkBuilder"/>: every gate
/// instance is checked against its group and model before any wiring is derived,
/// so an inconsistent input never reaches network assembly.
/// </summary>
public sealed partial class LogicNetworkBuilder
{
    /// <summary>The wiring role of one external pin of a gate group.</summary>
    private enum PinRole
    {
        Input,
        Output,
        Bias,
    }

    /// <summary>One resolved connection endpoint: a gate pin together with its role.</summary>
    private readonly record struct Endpoint(LogicPinRef Pin, PinRole Role);

    /// <summary>Rejects duplicated gate ids — network inputs and taps are named by gate id.</summary>
    private static void ThrowOnDuplicateGateIds(IReadOnlyList<GateContext> contexts)
    {
        var duplicate = contexts.GroupBy(c => c.GateId).FirstOrDefault(g => g.Count() > 1)?.Key;
        if (duplicate != null)
            throw new ArgumentException(
                $"Two gate groups are named '{duplicate}'. Gate ids come from the group name " +
                "and must be unique — rename one of the groups.",
                nameof(contexts));
    }

    /// <summary>
    /// Rejects a network input name that equals an output tap name: input names merge
    /// pins, output names rename taps, and a name shared across the two roles reads as
    /// the same wire in the Logic panel. Checking against every tap name (not only
    /// signal-named ones) also catches an input signal named like a raw
    /// <c>&lt;gate&gt;.&lt;pin&gt;</c> tap.
    /// </summary>
    private static void ThrowOnInputOutputNameCollision(
        IReadOnlyDictionary<string, List<string>> inputMembers,
        IReadOnlyDictionary<string, LogicPinRef> outputTaps)
    {
        var collision = inputMembers.Keys.FirstOrDefault(outputTaps.ContainsKey);
        if (collision == null)
            return;
        throw new ArgumentException(
            $"Signal name '{collision}' is used by input pins ({string.Join(", ", inputMembers[collision])}) " +
            $"and by output pin {collision} — rename one of them.");
    }

    /// <summary>One gate group with its prevalidated logic interface.</summary>
    private sealed class GateContext
    {
        private GateContext(LogicGateInstance instance)
        {
            Instance = instance;
        }

        /// <summary>The gate instance this context wraps.</summary>
        public LogicGateInstance Instance { get; }

        /// <summary>The network-local gate id: the group name.</summary>
        public string GateId => Instance.Group.GroupName;

        /// <summary>The placed gate group.</summary>
        public ComponentGroup Group => Instance.Group;

        /// <summary>The gate's evaluable logic model.</summary>
        public LogicGateModel Model => Instance.Model;

        /// <summary>Validates one gate instance and wraps it as a context.</summary>
        /// <exception cref="ArgumentException">The instance is incomplete or inconsistent.</exception>
        public static GateContext Create(LogicGateInstance? instance)
        {
            if (instance == null)
                throw new ArgumentException("A gate instance is null.", nameof(instance));
            if (instance.Group == null)
                throw new ArgumentException("A gate instance has no group.", nameof(instance));
            if (instance.Model == null)
                throw new ArgumentException($"Gate '{instance.Group.GroupName}' has no logic model.", nameof(instance));
            if (instance.Roles == null)
                throw new ArgumentException($"Gate '{instance.Group.GroupName}' has no role assignment.", nameof(instance));

            var context = new GateContext(instance);
            context.ThrowOnDuplicateRolePins();
            context.ThrowOnOverlappingRoles();
            context.ThrowOnRoleModelMismatch();
            context.ThrowOnUnknownRolePins();
            context.ThrowOnInvalidSignalNames(
                instance.Roles.InputSignalNames, instance.Roles.InputPinNames, "input");
            context.ThrowOnInvalidSignalNames(
                instance.Roles.OutputSignalNames, instance.Roles.OutputPinNames, "output");
            return context;
        }

        /// <summary>The wiring role of one external pin name, or null when the pin has no role.</summary>
        public PinRole? RoleOf(string pinName)
        {
            if (Instance.Roles.InputPinNames.Contains(pinName)) return PinRole.Input;
            if (Instance.Roles.OutputPinNames.Contains(pinName)) return PinRole.Output;
            if (Instance.Roles.BiasPinNames.Contains(pinName)) return PinRole.Bias;
            return null;
        }

        /// <summary>
        /// The network-signal name assigned to one input pin (issue #1025), or null
        /// when the pin carries none and keeps its own <c>&lt;gate&gt;.&lt;pin&gt;</c>
        /// name as its network input.
        /// </summary>
        public string? SignalNameOf(string pinName) =>
            SignalNameOf(Instance.Roles.InputSignalNames, pinName);

        /// <summary>
        /// The signal name assigned to one output pin, or null when the pin carries
        /// none and its network tap keeps the raw <c>&lt;gate&gt;.&lt;pin&gt;</c> name.
        /// </summary>
        public string? OutputSignalNameOf(string pinName) =>
            SignalNameOf(Instance.Roles.OutputSignalNames, pinName);

        /// <summary>Looks up one pin's signal name in an optional name map.</summary>
        private static string? SignalNameOf(IReadOnlyDictionary<string, string>? names, string pinName) =>
            names != null && names.TryGetValue(pinName, out var signalName)
                ? signalName
                : null;

        /// <summary>Rejects pins listed twice within one role list.</summary>
        private void ThrowOnDuplicateRolePins()
        {
            var duplicate = Instance.Roles.InputPinNames
                .Concat(Instance.Roles.OutputPinNames)
                .Concat(Instance.Roles.BiasPinNames)
                .GroupBy(name => name).FirstOrDefault(g => g.Count() > 1)?.Key;
            if (duplicate != null)
                throw new ArgumentException(
                    $"Gate '{GateId}' lists pin '{duplicate}' more than once in its role assignment.",
                    nameof(Instance));
        }

        /// <summary>Rejects pins claimed for two roles — a pin is exactly one of input, output, or bias.</summary>
        private void ThrowOnOverlappingRoles()
        {
            var roles = Instance.Roles;
            var overlap = roles.InputPinNames.Intersect(roles.OutputPinNames)
                .Concat(roles.InputPinNames.Intersect(roles.BiasPinNames))
                .Concat(roles.BiasPinNames.Intersect(roles.OutputPinNames))
                .FirstOrDefault();
            if (overlap != null)
                throw new ArgumentException(
                    $"Gate '{GateId}' assigns pin '{overlap}' two roles — a pin is exactly one of input, output, or bias.",
                    nameof(Instance));
        }

        /// <summary>Rejects a role assignment that does not match the gate model's interface.</summary>
        private void ThrowOnRoleModelMismatch()
        {
            ThrowOnSetMismatch("input", Instance.Roles.InputPinNames, Model.InputPinNames);
            ThrowOnSetMismatch("output", Instance.Roles.OutputPinNames, Model.OutputPinNames);
        }

        /// <summary>Compares one role list against the model's pin list, ignoring order.</summary>
        private void ThrowOnSetMismatch(string role, IReadOnlyList<string> rolePins, IReadOnlyList<string> modelPins)
        {
            if (rolePins.ToHashSet().SetEquals(modelPins))
                return;
            throw new ArgumentException(
                $"Gate '{GateId}' assigns {role} pins [{string.Join(", ", rolePins)}] but its logic model " +
                $"declares [{string.Join(", ", modelPins)}] — the roles must match the extraction the model came from.",
                nameof(Instance));
        }

        /// <summary>
        /// Rejects role pins the group does not expose. External pins without a role are
        /// fine — a group read as a smaller gate (the NOT reading of the NOT/NAND example
        /// leaves pin B unused) simply keeps them out of the logic network.
        /// </summary>
        private void ThrowOnUnknownRolePins()
        {
            var externalNames = Group.ExternalPins.Select(p => p.Name).ToList();
            var unknown = Instance.Roles.InputPinNames
                .Concat(Instance.Roles.OutputPinNames)
                .Concat(Instance.Roles.BiasPinNames)
                .FirstOrDefault(name => !externalNames.Contains(name));
            if (unknown != null)
                throw new ArgumentException(
                    $"Gate '{GateId}' assigns a role to pin '{unknown}', which the group does not expose. " +
                    $"Available pins: {string.Join(", ", externalNames)}.",
                    nameof(Instance));
        }

        /// <summary>
        /// Rejects empty signal names and signal names on pins that do not play the
        /// matching role — a signal name the network cannot honor would silently
        /// never merge (inputs) or never rename a tap (outputs).
        /// </summary>
        private void ThrowOnInvalidSignalNames(
            IReadOnlyDictionary<string, string>? signalNames,
            IReadOnlyList<string> rolePins,
            string role)
        {
            if (signalNames == null)
                return;
            foreach (var (pinName, signalName) in signalNames)
            {
                if (string.IsNullOrWhiteSpace(signalName))
                    throw new ArgumentException(
                        $"Gate '{GateId}' assigns pin '{pinName}' an empty signal name — " +
                        "a signal name must be a non-empty string.",
                        nameof(Instance));
                if (!rolePins.Contains(pinName))
                    throw new ArgumentException(
                        $"Gate '{GateId}' assigns pin '{pinName}' the signal name '{signalName}', " +
                        $"but '{pinName}' is not one of its {role} pins ({string.Join(", ", rolePins)}). " +
                        $"Only an {role} pin can carry a signal name.",
                        nameof(Instance));
            }
        }
    }
}
