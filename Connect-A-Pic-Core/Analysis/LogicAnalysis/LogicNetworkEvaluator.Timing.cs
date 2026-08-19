namespace CAP_Core.Analysis.LogicAnalysis;

/// <summary>
/// Timing half of <see cref="LogicNetworkEvaluator"/>: per-gate propagation delays in
/// picoseconds, per-wire delays for the waveguides between gates, and the critical
/// path — the longest cumulative delay from any network input to any output over the
/// gate DAG, summing gate delays and inter-gate wire delays along the path. The gate
/// delays arrive with the network (derived from each gate group's internal optical
/// path length, see <see cref="GateDelayCalculator"/>), the wire delays from the
/// connecting waveguide geometry (see <see cref="WireDelayCalculator"/>); the critical
/// path walks the same topological order <see cref="Evaluate"/> uses, so a gate's
/// cumulative delay is its own delay plus the slowest arrival (driver cumulative delay
/// plus that wire's delay) over its driven inputs. Networks built without delay data
/// report zero delays.
/// </summary>
public sealed partial class LogicNetworkEvaluator
{
    /// <summary>Propagation delay of every gate in picoseconds, keyed by gate id.</summary>
    public IReadOnlyDictionary<string, double> GateDelaysPicoseconds { get; private set; }
        = new Dictionary<string, double>();

    /// <summary>
    /// Propagation delay of every inter-gate wire in picoseconds, keyed by the
    /// (driver output → load input) edge. Exposed for future wire-delay visualization.
    /// </summary>
    public IReadOnlyDictionary<LogicWireEdge, double> WireDelaysPicoseconds { get; private set; }
        = new Dictionary<LogicWireEdge, double>();

    /// <summary>
    /// Total delay of the critical path in picoseconds: the longest cumulative delay
    /// from any network input to any network output, summing gate and wire delays.
    /// </summary>
    public double CriticalPathDelayPicoseconds { get; private set; }

    /// <summary>
    /// The gates on the critical path, ordered from the network input towards the
    /// output — the chain that limits how fast the network can clock.
    /// </summary>
    public IReadOnlyList<string> CriticalPathGateIds { get; private set; } = Array.Empty<string>();

    /// <summary>Stores the per-gate and per-wire delays and derives the critical path over the gate DAG.</summary>
    private void InitializeTiming(
        IReadOnlyDictionary<string, double>? gateDelays,
        IReadOnlyDictionary<LogicWireEdge, double>? wireDelays)
    {
        GateDelaysPicoseconds = BuildDelayMap(gateDelays);
        WireDelaysPicoseconds = BuildWireDelayMap(wireDelays);
        (CriticalPathDelayPicoseconds, CriticalPathGateIds) = ComputeCriticalPath();
    }

    /// <summary>Fills one delay per gate, rejecting unknown gates and implausible values.</summary>
    private IReadOnlyDictionary<string, double> BuildDelayMap(IReadOnlyDictionary<string, double>? gateDelays)
    {
        var delays = Gates.Keys.ToDictionary(id => id, _ => 0.0);
        if (gateDelays == null)
            return delays;

        foreach (var (gateId, delay) in gateDelays)
        {
            if (!Gates.ContainsKey(gateId))
                throw new ArgumentException(
                    $"A propagation delay was passed for unknown gate '{gateId}'. " +
                    $"Known gates: {string.Join(", ", Gates.Keys)}.",
                    nameof(gateDelays));
            if (double.IsNaN(delay) || double.IsInfinity(delay) || delay < 0)
                throw new ArgumentException(
                    $"Gate '{gateId}' has an invalid propagation delay of {delay} ps — " +
                    "a delay must be a finite, non-negative number.",
                    nameof(gateDelays));
            delays[gateId] = delay;
        }
        return delays;
    }

    /// <summary>
    /// Fills one delay per inter-gate wire, rejecting edges the wiring does not
    /// contain and implausible values.
    /// </summary>
    private IReadOnlyDictionary<LogicWireEdge, double> BuildWireDelayMap(
        IReadOnlyDictionary<LogicWireEdge, double>? wireDelays)
    {
        var delays = _inputWiring
            .Where(pair => pair.Value is LogicNetDriver.GateOutput)
            .ToDictionary(
                pair => new LogicWireEdge(((LogicNetDriver.GateOutput)pair.Value).Pin, pair.Key),
                _ => 0.0);
        if (wireDelays == null)
            return delays;

        foreach (var (edge, delay) in wireDelays)
        {
            if (!delays.ContainsKey(edge))
                throw new ArgumentException(
                    $"A wire delay was passed for edge '{FormatPin(edge.Source)}' → '{FormatPin(edge.Load)}', " +
                    "which the network wiring does not contain.",
                    nameof(wireDelays));
            if (double.IsNaN(delay) || double.IsInfinity(delay) || delay < 0)
                throw new ArgumentException(
                    $"Wire '{FormatPin(edge.Source)}' → '{FormatPin(edge.Load)}' has an invalid propagation delay " +
                    $"of {delay} ps — a delay must be a finite, non-negative number.",
                    nameof(wireDelays));
            delays[edge] = delay;
        }
        return delays;
    }

    /// <summary>
    /// Accumulates delays in topological order — a gate's cumulative delay is its own
    /// delay plus the slowest arrival over its driven inputs (driver cumulative delay
    /// plus that wire's delay) — then backtracks from the slowest output tap through
    /// the slowest-driver chain to the network input.
    /// </summary>
    private (double Delay, IReadOnlyList<string> Path) ComputeCriticalPath()
    {
        var cumulative = new Dictionary<string, double>();
        var predecessor = new Dictionary<string, string?>();
        foreach (var gateId in _evaluationOrder)
        {
            var (driverDelay, driverId) = SlowestDriver(gateId, cumulative);
            cumulative[gateId] = driverDelay + GateDelaysPicoseconds[gateId];
            predecessor[gateId] = driverId;
        }

        var criticalGate = _outputTaps.Values
            .OrderByDescending(pin => cumulative[pin.GateId])
            .First().GateId;
        return (cumulative[criticalGate], Backtrack(criticalGate, predecessor));
    }

    /// <summary>
    /// The slowest arrival at one of this gate's inputs: driver cumulative delay
    /// plus wire delay. A register gate reports no arrival — its inputs are sampled
    /// at the clock step, so the register breaks the combinational critical path
    /// the way its output starts a new one.
    /// </summary>
    private (double Delay, string? GateId) SlowestDriver(
        string gateId, IReadOnlyDictionary<string, double> cumulative)
    {
        if (IsRegisterGate(gateId))
            return (0.0, null);
        var delay = 0.0;
        string? driverId = null;
        foreach (var pinName in Gates[gateId].InputPinNames)
        {
            var load = new LogicPinRef(gateId, pinName);
            if (_inputWiring[load] is not LogicNetDriver.GateOutput source)
                continue;
            var arrival = cumulative[source.Pin.GateId]
                + WireDelaysPicoseconds[new LogicWireEdge(source.Pin, load)];
            if (arrival > delay)
            {
                delay = arrival;
                driverId = source.Pin.GateId;
            }
        }
        return (delay, driverId);
    }

    /// <summary>Walks the slowest-driver chain from the critical gate back to the network input.</summary>
    private static IReadOnlyList<string> Backtrack(string gateId, IReadOnlyDictionary<string, string?> predecessor)
    {
        var path = new List<string>();
        for (string? current = gateId; current != null; current = predecessor[current])
            path.Add(current);
        path.Reverse();
        return path;
    }
}
