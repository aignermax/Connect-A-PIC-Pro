namespace CAP_Core.Analysis.LogicAnalysis;

/// <summary>
/// Evaluates a combinational network of <see cref="LogicGateModel"/> instances:
/// gates wired output→input as a simple net list of (gateId, pin) pairs. The
/// network is validated and topologically ordered once at construction — cycles
/// are rejected with a clear exception, since sequential logic is a later rung —
/// and <see cref="Evaluate"/> then walks the gates in dependency order. Every
/// stage output is a clean bit taken from the gate's truth table, so the network
/// has ideal level restoration by construction: arbitrary cascade depth works,
/// exactly what the passive-linear layer cannot do.
/// </summary>
public sealed partial class LogicNetworkEvaluator
{
    private readonly IReadOnlyDictionary<LogicPinRef, LogicNetDriver> _inputWiring;
    private readonly IReadOnlyDictionary<string, LogicPinRef> _outputTaps;
    private readonly IReadOnlyList<string> _evaluationOrder;

    /// <summary>
    /// Assembles and validates a combinational logic network.
    /// </summary>
    /// <param name="inputPinNames">Network-level input pin names (at least one, no duplicates).</param>
    /// <param name="gates">The gate instances of the network, keyed by their network-local id.</param>
    /// <param name="inputWiring">
    /// The driver of every gate input pin, keyed by (gateId, inputPin). Every input pin of
    /// every gate must appear exactly once; fan-out of a driver to several loads is allowed.
    /// </param>
    /// <param name="outputTaps">Network-level output names, each tapping one gate output pin.</param>
    /// <exception cref="ArgumentException">
    /// A gate, pin, or network input is unknown; a gate input is left undriven; or the
    /// wiring is otherwise inconsistent. The message names the offending element.
    /// </exception>
    /// <exception cref="InvalidOperationException">The wiring forms a cycle.</exception>
    public LogicNetworkEvaluator(
        IReadOnlyList<string> inputPinNames,
        IReadOnlyDictionary<string, LogicGateModel> gates,
        IReadOnlyDictionary<LogicPinRef, LogicNetDriver> inputWiring,
        IReadOnlyDictionary<string, LogicPinRef> outputTaps)
    {
        InputPinNames = inputPinNames ?? throw new ArgumentNullException(nameof(inputPinNames));
        Gates = gates ?? throw new ArgumentNullException(nameof(gates));
        _inputWiring = inputWiring ?? throw new ArgumentNullException(nameof(inputWiring));
        _outputTaps = outputTaps ?? throw new ArgumentNullException(nameof(outputTaps));

        ValidateNetworkInputs();
        ValidateGates();
        ValidateWiring();
        ValidateOutputTaps();
        _evaluationOrder = TopologicalOrder();
    }

    /// <summary>Network-level input pin names.</summary>
    public IReadOnlyList<string> InputPinNames { get; }

    /// <summary>The gate instances of the network, keyed by their network-local id.</summary>
    public IReadOnlyDictionary<string, LogicGateModel> Gates { get; }

    /// <summary>Network-level output names, in declaration order.</summary>
    public IReadOnlyList<string> OutputPinNames => _outputTaps.Keys.ToList();

    /// <summary>
    /// Evaluates the network for one input combination: every gate fires exactly
    /// once in topological order, reading clean bits and producing clean bits.
    /// </summary>
    /// <param name="inputBits">One bit per network-level input pin name.</param>
    /// <returns>The bit of every network-level output.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="inputBits"/> is null.</exception>
    /// <exception cref="ArgumentException">A network input bit is missing or an unknown pin is passed.</exception>
    public IReadOnlyDictionary<string, bool> Evaluate(IReadOnlyDictionary<string, bool> inputBits)
    {
        if (inputBits == null) throw new ArgumentNullException(nameof(inputBits));
        ValidateInputBits(inputBits);

        var gateOutputs = new Dictionary<LogicPinRef, bool>();
        foreach (var gateId in _evaluationOrder)
        {
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

        return _outputTaps.ToDictionary(tap => tap.Key, tap => gateOutputs[tap.Value]);
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
    /// Orders the gates so every gate fires only after all gates driving its inputs
    /// have fired (Kahn's algorithm). A network that cannot be fully ordered contains
    /// a cycle — sequential logic, which this combinational evaluator rejects.
    /// </summary>
    private IReadOnlyList<string> TopologicalOrder()
    {
        var dependencies = Gates.Keys.ToDictionary(id => id, _ => new HashSet<string>());
        foreach (var (load, driver) in _inputWiring)
        {
            if (driver is LogicNetDriver.GateOutput source)
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
