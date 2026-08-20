namespace CAP_Core.Analysis.LogicAnalysis;

/// <summary>
/// Evaluates a network of <see cref="LogicGateModel"/> instances: gates wired
/// output→input as a simple net list of (gateId, pin) pairs. The network is
/// validated and topologically ordered once at construction. A purely combinational
/// network (a DAG) settles in one <see cref="Evaluate"/> walk in dependency order.
/// Gates designated as <b>registers</b> turn the evaluation two-phase: their
/// outputs hold the last committed value while the combinational logic around them
/// settles, and an explicit <see cref="Step"/> samples their inputs and commits
/// them (D-semantics) — the behavioral abstraction behind sequential logic
/// (registers, latches, later a program counter). A feedback cycle is legal exactly
/// when it passes through at least one register; a cycle through combinational
/// gates only keeps the honest rejection, since an idealized combinational loop has
/// no settling order. Every stage output is a clean bit taken from the gate's
/// truth table, so the network has ideal level restoration by construction:
/// arbitrary cascade depth works, exactly what the passive-linear layer cannot do.
/// </summary>
public sealed partial class LogicNetworkEvaluator
{
    private readonly IReadOnlyDictionary<LogicPinRef, LogicNetDriver> _inputWiring;
    private readonly IReadOnlyDictionary<string, LogicPinRef> _outputTaps;
    private readonly IReadOnlyList<string> _evaluationOrder;

    /// <summary>
    /// Assembles and validates a logic network.
    /// </summary>
    /// <param name="inputPinNames">Network-level input pin names (at least one, no duplicates).</param>
    /// <param name="gates">The gate instances of the network, keyed by their network-local id.</param>
    /// <param name="inputWiring">
    /// The driver of every gate input pin, keyed by (gateId, inputPin). Every input pin of
    /// every gate must appear exactly once; fan-out of a driver to several loads is allowed.
    /// </param>
    /// <param name="outputTaps">Network-level output names, each tapping one gate output pin.</param>
    /// <param name="gateDelays">
    /// Optional propagation delay per gate in picoseconds (see
    /// <see cref="GateDelayCalculator"/>); gates without an entry report zero delay.
    /// </param>
    /// <param name="wireDelays">
    /// Optional propagation delay per inter-gate wire in picoseconds (see
    /// <see cref="WireDelayCalculator"/>), keyed by the (driver output → load input)
    /// edge; edges without an entry report zero delay.
    /// </param>
    /// <param name="registerGateIds">
    /// Optional ids of the gates designated as behavioral register state elements:
    /// their outputs hold the last committed value during <see cref="Evaluate"/> and
    /// advance only on <see cref="Step"/>. Feedback cycles through them are legal.
    /// </param>
    /// <exception cref="ArgumentException">
    /// A gate, pin, or network input is unknown; a gate input is left undriven; or the
    /// wiring is otherwise inconsistent. The message names the offending element.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The wiring forms a cycle that passes through no register gate.
    /// </exception>
    public LogicNetworkEvaluator(
        IReadOnlyList<string> inputPinNames,
        IReadOnlyDictionary<string, LogicGateModel> gates,
        IReadOnlyDictionary<LogicPinRef, LogicNetDriver> inputWiring,
        IReadOnlyDictionary<string, LogicPinRef> outputTaps,
        IReadOnlyDictionary<string, double>? gateDelays = null,
        IReadOnlyDictionary<LogicWireEdge, double>? wireDelays = null,
        IReadOnlyCollection<string>? registerGateIds = null)
    {
        InputPinNames = inputPinNames ?? throw new ArgumentNullException(nameof(inputPinNames));
        Gates = gates ?? throw new ArgumentNullException(nameof(gates));
        _inputWiring = inputWiring ?? throw new ArgumentNullException(nameof(inputWiring));
        _outputTaps = outputTaps ?? throw new ArgumentNullException(nameof(outputTaps));

        ValidateNetworkInputs();
        ValidateGates();
        ValidateWiring();
        ValidateOutputTaps();
        InitializeRegisters(registerGateIds);
        _evaluationOrder = TopologicalOrder();
        DetectFanOut();
        InitializeTiming(gateDelays, wireDelays);
    }

    /// <summary>Network-level input pin names.</summary>
    public IReadOnlyList<string> InputPinNames { get; }

    /// <summary>The gate instances of the network, keyed by their network-local id.</summary>
    public IReadOnlyDictionary<string, LogicGateModel> Gates { get; }

    /// <summary>Network-level output names, in declaration order.</summary>
    public IReadOnlyList<string> OutputPinNames => _outputTaps.Keys.ToList();

    /// <summary>Network-level output taps: the tapped gate output pin per output name.</summary>
    public IReadOnlyDictionary<string, LogicPinRef> OutputTaps => _outputTaps;

    /// <summary>The driver of every gate input pin — exposed for the event-timeline walker.</summary>
    internal IReadOnlyDictionary<LogicPinRef, LogicNetDriver> InputWiring => _inputWiring;

    /// <summary>The topological gate order — exposed for the event-timeline walker.</summary>
    internal IReadOnlyList<string> EvaluationOrder => _evaluationOrder;

    /// <summary>
    /// Evaluates the network for one input combination: every combinational gate
    /// fires exactly once in topological order, reading clean bits and producing
    /// clean bits. Register gates do not fire — their outputs hold the last value
    /// committed by <see cref="Step"/> (initially logic 0), so changing a register's
    /// input never changes its output within a settling phase.
    /// </summary>
    /// <param name="inputBits">One bit per network-level input pin name.</param>
    /// <returns>The bit of every network-level output.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="inputBits"/> is null.</exception>
    /// <exception cref="ArgumentException">A network input bit is missing or an unknown pin is passed.</exception>
    public IReadOnlyDictionary<string, bool> Evaluate(IReadOnlyDictionary<string, bool> inputBits)
    {
        if (inputBits == null) throw new ArgumentNullException(nameof(inputBits));
        ValidateInputBits(inputBits);

        var gateOutputs = EvaluateGateOutputs(inputBits);
        CaptureSettledState(inputBits, gateOutputs);
        return _outputTaps.ToDictionary(tap => tap.Key, tap => gateOutputs[tap.Value]);
    }

    /// <summary>
    /// Settles every gate output pin (not just the network taps) for one validated
    /// input combination: register outputs enter pre-seeded with their committed
    /// state and the combinational gates then fire once in topological order.
    /// Shared with the event-timeline walker so both see the same settled values.
    /// </summary>
    internal Dictionary<LogicPinRef, bool> EvaluateGateOutputs(IReadOnlyDictionary<string, bool> inputBits)
    {
        var gateOutputs = CommittedRegisterOutputs();
        foreach (var gateId in _evaluationOrder)
        {
            if (IsRegisterGate(gateId))
                continue;
            var gate = Gates[gateId];
            var gateInputBits = new Dictionary<string, bool>(gate.InputPinNames.Count);
            foreach (var pinName in gate.InputPinNames)
            {
                gateInputBits[pinName] = ResolveDriverBit(
                    _inputWiring[new LogicPinRef(gateId, pinName)], inputBits, gateOutputs);
            }

            foreach (var (pinName, bit) in gate.Evaluate(gateInputBits))
            {
                gateOutputs[new LogicPinRef(gateId, pinName)] = bit;
            }
        }
        return gateOutputs;
    }

    /// <summary>Resolves one driver to its current bit: a network input bit or an already-evaluated gate output.</summary>
    private static bool ResolveDriverBit(
        LogicNetDriver driver,
        IReadOnlyDictionary<string, bool> inputBits,
        IReadOnlyDictionary<LogicPinRef, bool> gateOutputs) =>
        driver switch
        {
            LogicNetDriver.NetworkInput input => inputBits[input.PinName],
            LogicNetDriver.GateOutput source => gateOutputs[source.Pin],
            _ => throw new InvalidOperationException($"Unsupported driver type '{driver.GetType().Name}'."),
        };

    /// <summary>
    /// Orders the gates so every combinational gate fires only after all gates
    /// driving its inputs have fired (Kahn's algorithm). Edges into a register gate
    /// take no part in the ordering — the register samples them at the clock step,
    /// not during settling — so a feedback cycle passing through a register
    /// resolves. A network that still cannot be fully ordered contains a purely
    /// combinational cycle, which has no settling order and stays rejected.
    /// </summary>
    private IReadOnlyList<string> TopologicalOrder()
    {
        var dependencies = Gates.Keys.ToDictionary(id => id, _ => new HashSet<string>());
        foreach (var (load, driver) in _inputWiring)
        {
            if (driver is LogicNetDriver.GateOutput source && !IsRegisterGate(load.GateId))
                dependencies[load.GateId].Add(source.Pin.GateId);
        }

        var order = new List<string>(Gates.Count);
        var resolved = new HashSet<string>();
        var remaining = Gates.Keys.ToList();
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(id => dependencies[id].IsSubsetOf(resolved)).ToList();
            if (ready.Count == 0)
                throw new InvalidOperationException(
                    $"The logic network contains a cycle involving gates: {string.Join(", ", remaining)}. " +
                    "Only combinational networks (DAGs) can be evaluated; sequential logic is not supported.");
            foreach (var id in ready)
            {
                order.Add(id);
                resolved.Add(id);
                remaining.Remove(id);
            }
        }
        return order;
    }
}
