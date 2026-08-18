namespace CAP_Core.Analysis.LogicAnalysis;

/// <summary>
/// Timing half of <see cref="LogicNetworkEvaluator"/>: per-gate propagation delays in
/// picoseconds and the critical path — the longest cumulative delay from any network
/// input to any output over the gate DAG. The delays arrive with the network (derived
/// from each gate group's internal optical path length, see
/// <see cref="GateDelayCalculator"/>); the critical path walks the same topological
/// order <see cref="Evaluate"/> uses, so a gate's cumulative delay is its own delay
/// plus the slowest driving gate's cumulative delay. Networks built without delay
/// data report zero delays. Wire delays between gates are not modeled yet — every
/// logic wire is still ideal.
/// </summary>
public sealed partial class LogicNetworkEvaluator
{
    /// <summary>Propagation delay of every gate in picoseconds, keyed by gate id.</summary>
    public IReadOnlyDictionary<string, double> GateDelaysPicoseconds { get; private set; }
        = new Dictionary<string, double>();

    /// <summary>
    /// Total delay of the critical path in picoseconds: the longest cumulative delay
    /// from any network input to any network output.
    /// </summary>
    public double CriticalPathDelayPicoseconds { get; private set; }

    /// <summary>
    /// The gates on the critical path, ordered from the network input towards the
    /// output — the chain that limits how fast the network can clock.
    /// </summary>
    public IReadOnlyList<string> CriticalPathGateIds { get; private set; } = Array.Empty<string>();

    /// <summary>Stores the per-gate delays and derives the critical path over the gate DAG.</summary>
    private void InitializeTiming(IReadOnlyDictionary<string, double>? gateDelays)
    {
        GateDelaysPicoseconds = BuildDelayMap(gateDelays);
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
    /// Accumulates delays in topological order, then backtracks from the slowest output
    /// tap through the slowest-driver chain to the network input.
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

    /// <summary>The cumulative delay of the slowest gate driving one of this gate's inputs.</summary>
    private (double Delay, string? GateId) SlowestDriver(
        string gateId, IReadOnlyDictionary<string, double> cumulative)
    {
        var delay = 0.0;
        string? driverId = null;
        foreach (var pinName in Gates[gateId].InputPinNames)
        {
            if (_inputWiring[new LogicPinRef(gateId, pinName)] is not LogicNetDriver.GateOutput source)
                continue;
            if (cumulative[source.Pin.GateId] > delay)
            {
                delay = cumulative[source.Pin.GateId];
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
