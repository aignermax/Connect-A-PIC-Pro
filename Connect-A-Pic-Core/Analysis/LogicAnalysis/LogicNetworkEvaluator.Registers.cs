namespace CAP_Core.Analysis.LogicAnalysis;

/// <summary>
/// Register state half of <see cref="LogicNetworkEvaluator"/>: the gates designated
/// as behavioral register state elements, their committed output values, and the
/// explicit clock <see cref="Step"/>. A register's outputs hold their last
/// committed value while the combinational logic around them settles
/// (<see cref="LogicNetworkEvaluator.Evaluate"/>); only <see cref="Step"/> samples
/// the register inputs from the settled network and commits the sampled values as
/// the new state (D-semantics). All registers commit simultaneously from the same
/// settled state, so a cross-coupled pair (an SR latch) commits race-free.
/// This is a deliberate behavioral abstraction at the logic level (roadmap
/// principle 4): the physical mapping of a register (e.g. an SA-based latch on an
/// InP platform) comes later — no fake optics. Registers power up cleared
/// (logic 0 on every output pin) — a documented convention of the behavioral
/// model, not a physical claim.
/// </summary>
public sealed partial class LogicNetworkEvaluator
{
    private readonly HashSet<string> _registerGateIds = new();
    private readonly Dictionary<LogicPinRef, bool> _committedRegisterOutputs = new();
    private IReadOnlyDictionary<string, bool>? _lastInputBits;
    private IReadOnlyDictionary<LogicPinRef, bool>? _lastSettledOutputs;

    /// <summary>
    /// The committed state of every register output pin. Empty for a purely
    /// combinational network. Read-only: the state advances only through
    /// <see cref="Step"/>.
    /// </summary>
    public IReadOnlyDictionary<LogicPinRef, bool> RegisterState => _committedRegisterOutputs;

    /// <summary>Whether the gate is a designated register state element.</summary>
    internal bool IsRegisterGate(string gateId) => _registerGateIds.Contains(gateId);

    /// <summary>
    /// Advances the network by one clock step: every register samples its input
    /// pins from the settled state of the last <see cref="Evaluate"/> call and
    /// commits the sampled table outputs as its new state. All registers sample the
    /// same pre-step state (simultaneous commit, D-FF semantics), and the network
    /// is re-settled afterwards, so consecutive steps advance consecutive clocks.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <see cref="Evaluate"/> was never called — there is no settled state to sample.
    /// </exception>
    public void Step()
    {
        if (_lastInputBits == null || _lastSettledOutputs == null)
            throw new InvalidOperationException(
                "Step() requires a settled network: call Evaluate at least once before the " +
                "first Step, so the register inputs have values to sample.");
        if (_registerGateIds.Count == 0)
            return;

        var sampled = new Dictionary<LogicPinRef, bool>();
        foreach (var gateId in _registerGateIds)
        {
            var gate = Gates[gateId];
            var gateInputBits = new Dictionary<string, bool>(gate.InputPinNames.Count);
            foreach (var pinName in gate.InputPinNames)
            {
                gateInputBits[pinName] = ResolveDriverBit(
                    _inputWiring[new LogicPinRef(gateId, pinName)], _lastInputBits, _lastSettledOutputs);
            }
            foreach (var (pinName, bit) in gate.Evaluate(gateInputBits))
            {
                sampled[new LogicPinRef(gateId, pinName)] = bit;
            }
        }

        foreach (var (pin, bit) in sampled)
        {
            _committedRegisterOutputs[pin] = bit;
        }
        _lastSettledOutputs = EvaluateGateOutputs(_lastInputBits);
    }

    /// <summary>
    /// Registers the designated register gates and powers their state up cleared.
    /// Runs before the topological ordering, which skips edges into registers.
    /// </summary>
    /// <exception cref="ArgumentException">A register id names no gate of the network.</exception>
    private void InitializeRegisters(IReadOnlyCollection<string>? registerGateIds)
    {
        if (registerGateIds == null)
            return;
        foreach (var gateId in registerGateIds)
        {
            if (!Gates.ContainsKey(gateId))
                throw new ArgumentException(
                    $"The register designation names unknown gate '{gateId}'. " +
                    $"Known gates: {string.Join(", ", Gates.Keys)}.",
                    nameof(registerGateIds));
            _registerGateIds.Add(gateId);
            foreach (var pinName in Gates[gateId].OutputPinNames)
            {
                _committedRegisterOutputs[new LogicPinRef(gateId, pinName)] = false;
            }
        }
    }

    /// <summary>A copy of the committed register outputs, seeding one settling pass.</summary>
    private Dictionary<LogicPinRef, bool> CommittedRegisterOutputs() => new(_committedRegisterOutputs);

    /// <summary>
    /// Remembers the settled state <see cref="Step"/> samples from. The input bits
    /// are copied so a caller mutating its dictionary cannot rewrite history.
    /// </summary>
    private void CaptureSettledState(
        IReadOnlyDictionary<string, bool> inputBits,
        IReadOnlyDictionary<LogicPinRef, bool> settledOutputs)
    {
        _lastInputBits = new Dictionary<string, bool>(inputBits);
        _lastSettledOutputs = settledOutputs;
    }
}
