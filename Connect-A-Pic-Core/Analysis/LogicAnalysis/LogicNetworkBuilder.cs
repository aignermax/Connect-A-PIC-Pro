using CAP_Core.Components.ComponentHelpers;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;

namespace CAP_Core.Analysis.LogicAnalysis;

/// <summary>
/// Derives a <see cref="LogicNetworkEvaluator"/> from the gate groups placed on the
/// canvas, making the canvas the source of truth for the wiring: a connection from
/// one group's external output pin to another group's external input pin becomes a
/// logic wire (fan-out of one output to several inputs is allowed). Unconnected
/// gate input pins become network-level inputs: pins carrying a persisted signal
/// name (issue #1025) merge into one network input per signal — the full adder's
/// thirteen addend-A pins become the single input <c>A</c> — while a pin without a
/// signal name keeps its own <c>&lt;group&gt;.&lt;pin&gt;</c> name and never merges
/// by bare pin name. Every gate output pin becomes a network-level output tap named
/// <c>&lt;group&gt;.&lt;pin&gt;</c> — also when it additionally drives another gate.
/// Bias pins take no part in wiring (they are constantly on — the extraction
/// contract); a connection into a bias pin, a connection between two input pins,
/// and a gate input driven by two different outputs are rejected with messages
/// naming the pins.
/// </summary>
public sealed partial class LogicNetworkBuilder
{
    private readonly GateDelayCalculator _delayCalculator = new();
    private readonly WireDelayCalculator _wireDelayCalculator = new();

    /// <summary>
    /// Derives and validates the logic network behind the given gate groups and the
    /// design's connections between their external pins.
    /// </summary>
    /// <param name="gates">
    /// The top-level gate groups, each with its logic model and role assignment.
    /// Gate ids come from the group names and must be unique.
    /// </param>
    /// <param name="connections">
    /// The design's waveguide connections. Only connections joining external pins of
    /// two gate groups take part in wiring; connections towards anything else (a laser,
    /// an external port, an ungrouped component) are ignored — an unconnected gate
    /// input simply becomes a network-level input.
    /// </param>
    /// <param name="wavelengthNm">
    /// Wavelength in nm the per-gate propagation delays are derived at; defaults to
    /// the standard red wavelength when not provided.
    /// </param>
    /// <returns>The validated, evaluation-ready network.</returns>
    /// <exception cref="ArgumentException">
    /// A gate id is duplicated, a role assignment does not match its model or group,
    /// or a connection is logically invalid. The message names the offending pins.
    /// </exception>
    /// <exception cref="InvalidOperationException">The derived wiring forms a cycle.</exception>
    public LogicNetworkEvaluator Build(
        IReadOnlyList<LogicGateInstance> gates,
        IReadOnlyList<WaveguideConnection> connections,
        double? wavelengthNm = null)
    {
        if (gates == null) throw new ArgumentNullException(nameof(gates));
        if (connections == null) throw new ArgumentNullException(nameof(connections));
        if (gates.Count == 0)
            throw new ArgumentException("A logic network needs at least one gate group.", nameof(gates));

        var contexts = gates.Select(GateContext.Create).ToList();
        ThrowOnDuplicateGateIds(contexts);

        var drivers = new Dictionary<LogicPinRef, LogicPinRef>();
        var edgeConnections = new Dictionary<LogicPinRef, WaveguideConnection>();
        foreach (var connection in connections)
        {
            AddConnectionDrivers(contexts, connection, drivers, edgeConnections);
        }

        return AssembleNetwork(contexts, drivers, edgeConnections, wavelengthNm ?? StandardWaveLengths.RedNM);
    }

    /// <summary>Classifies one design connection and records the logic driver and wire it implies, if any.</summary>
    private static void AddConnectionDrivers(
        IReadOnlyList<GateContext> contexts,
        WaveguideConnection connection,
        IDictionary<LogicPinRef, LogicPinRef> drivers,
        IDictionary<LogicPinRef, WaveguideConnection> edgeConnections)
    {
        var start = ResolveEndpoint(contexts, connection.StartPin);
        var end = ResolveEndpoint(contexts, connection.EndPin);
        if (start == null || end == null)
            return;

        var (source, load) = Classify(start.Value, end.Value);
        if (drivers.TryGetValue(load, out var existing) && !existing.Equals(source))
            throw new ArgumentException(
                $"Gate input '{Format(load)}' is driven by two different gate outputs: " +
                $"'{Format(existing)}' and '{Format(source)}'. One logic wire needs exactly one driver.");
        drivers[load] = source;
        edgeConnections.TryAdd(load, connection);
    }

    /// <summary>Determines which endpoint drives which, rejecting logically invalid pairings.</summary>
    private static (LogicPinRef Source, LogicPinRef Load) Classify(Endpoint first, Endpoint second)
    {
        if (first.Role == PinRole.Bias || second.Role == PinRole.Bias)
            throw new ArgumentException(
                $"Connection between '{Format(first.Pin)}' and '{Format(second.Pin)}' touches a bias pin. " +
                "Bias pins are constantly on and take no part in wiring — remove the connection.");
        if (first.Role == PinRole.Output && second.Role == PinRole.Input)
            return (first.Pin, second.Pin);
        if (second.Role == PinRole.Output && first.Role == PinRole.Input)
            return (second.Pin, first.Pin);
        if (first.Role == PinRole.Input)
            throw new ArgumentException(
                $"Connection joins two gate input pins: '{Format(first.Pin)}' and '{Format(second.Pin)}'. " +
                "A gate input must be driven by a gate output or left unconnected to become a network input.");
        throw new ArgumentException(
            $"Connection joins two gate output pins: '{Format(first.Pin)}' and '{Format(second.Pin)}'. " +
            "A gate output drives gate inputs; it cannot be driven itself.");
    }

    /// <summary>Renders a gate pin the way network inputs and taps are named: <c>group.pin</c>.</summary>
    private static string Format(LogicPinRef pin) => $"{pin.GateId}.{pin.PinName}";

    /// <summary>
    /// Maps one connection endpoint onto its gate pin and role, or null when no gate
    /// group is involved. The load path (and live canvas wiring) binds a wire endpoint
    /// to the internal component pin behind a group's external pin — those resolve
    /// through the external pin's name, so a loaded design assembles straight from
    /// its own connections.
    /// </summary>
    private static Endpoint? ResolveEndpoint(IReadOnlyList<GateContext> contexts, PhysicalPin? pin)
    {
        if (pin?.ParentComponent == null)
            return null;
        var context = contexts.FirstOrDefault(c => ReferenceEquals(c.Group, pin.ParentComponent));
        if (context != null)
            return ToEndpoint(context, pin.Name);

        foreach (var candidate in contexts)
        {
            var external = candidate.Group.ExternalPins.FirstOrDefault(p => ReferenceEquals(p.InternalPin, pin));
            if (external != null)
                return ToEndpoint(candidate, external.Name);
        }
        return null;
    }

    /// <summary>Wraps one gate pin name as an endpoint, or null when the pin carries no role.</summary>
    private static Endpoint? ToEndpoint(GateContext context, string pinName)
    {
        var role = context.RoleOf(pinName);
        return role == null ? null : new Endpoint(new LogicPinRef(context.GateId, pinName), role.Value);
    }

    /// <summary>
    /// Assembles the evaluator: unconnected gate inputs become network-level inputs
    /// (one per signal name, see <see cref="NetworkInputName"/>), and every gate
    /// output pin becomes a network-level output tap named
    /// <c>&lt;group&gt;.&lt;pin&gt;</c>. Each gate's propagation delay is derived
    /// from its group's internal optical path length, and each inter-gate wire's
    /// delay from the connecting waveguide's routed path.
    /// </summary>
    private LogicNetworkEvaluator AssembleNetwork(
        IReadOnlyList<GateContext> contexts,
        IReadOnlyDictionary<LogicPinRef, LogicPinRef> drivers,
        IReadOnlyDictionary<LogicPinRef, WaveguideConnection> edgeConnections,
        double wavelengthNm)
    {
        var networkInputs = new List<string>();
        var wiring = new Dictionary<LogicPinRef, LogicNetDriver>();
        var outputTaps = new Dictionary<string, LogicPinRef>();
        var models = new Dictionary<string, LogicGateModel>();
        var delays = new Dictionary<string, double>();
        var wireDelays = new Dictionary<LogicWireEdge, double>();

        foreach (var (load, connection) in edgeConnections)
        {
            wireDelays[new LogicWireEdge(drivers[load], load)] =
                _wireDelayCalculator.CalculatePicoseconds(connection, wavelengthNm);
        }

        foreach (var context in contexts)
        {
            models[context.GateId] = context.Model;
            delays[context.GateId] = _delayCalculator.CalculatePicoseconds(context.Group, wavelengthNm);
            foreach (var pinName in context.Model.InputPinNames)
            {
                var load = new LogicPinRef(context.GateId, pinName);
                wiring[load] = drivers.TryGetValue(load, out var source)
                    ? new LogicNetDriver.GateOutput(source)
                    : new LogicNetDriver.NetworkInput(NetworkInputName(networkInputs, context, load));
            }
            foreach (var pinName in context.Model.OutputPinNames)
            {
                outputTaps[$"{context.GateId}.{pinName}"] = new LogicPinRef(context.GateId, pinName);
            }
        }

        return new LogicNetworkEvaluator(networkInputs, models, wiring, outputTaps, delays, wireDelays);
    }

    /// <summary>
    /// Registers and returns the network-level input name of an unconnected gate
    /// input: the pin's persisted signal name when it carries one — every member pin
    /// of the signal maps to the same network input, registered once — else the
    /// pin's own <c>&lt;group&gt;.&lt;pin&gt;</c> name (no merging by bare pin name).
    /// </summary>
    private static string NetworkInputName(
        ICollection<string> networkInputs, GateContext context, LogicPinRef load)
    {
        var name = context.SignalNameOf(load.PinName) ?? Format(load);
        if (!networkInputs.Contains(name))
            networkInputs.Add(name);
        return name;
    }
}
