namespace CAP_Core.Analysis.LogicAnalysis;

/// <summary>
/// One gate output pin switching to a new logic level at one point in time. The
/// timeline any future execution visualizer consumes: when the network inputs
/// toggle, this is what propagates through the gate DAG.
/// </summary>
/// <param name="TimePicoseconds">Absolute time of the switch, counted from the input toggle at t = 0.</param>
/// <param name="GateId">The network-local id of the gate whose output switches.</param>
/// <param name="OutputPin">The output pin of that gate producing the new level.</param>
/// <param name="NewValue">The logic level the pin switches to.</param>
public sealed record LogicSwitchEvent(
    double TimePicoseconds,
    string GateId,
    string OutputPin,
    bool NewValue);

/// <summary>
/// Event-driven view of one <see cref="LogicNetworkEvaluator"/> transition: given
/// the input assignment before and after a toggle, produces the ordered list of
/// per-gate switch events. A network input changes at t = 0; a gate output
/// switches at <c>max(arrival over its inputs) + gateDelay</c>, where an arrival
/// is the driver's switch time plus that wire's delay (a driver that never
/// switches contributes a stable arrival of 0). Gates whose output value does
/// not change emit no event. This slice models exactly one switch per pin — no
/// glitch/hazard modeling — and reuses the evaluator's
/// <see cref="LogicNetworkEvaluator.GateDelaysPicoseconds"/> and
/// <see cref="LogicNetworkEvaluator.WireDelaysPicoseconds"/> without recomputing
/// any physics.
/// </summary>
public static class LogicEventTimeline
{
    /// <summary>
    /// Computes the ordered switch events between two input assignments.
    /// </summary>
    /// <param name="network">The network to walk; supplies gates, wiring, and delays.</param>
    /// <param name="previousInputs">The input assignment before the toggle.</param>
    /// <param name="nextInputs">The input assignment after the toggle.</param>
    /// <returns>
    /// The switch events sorted by time, ties broken by gate id, then by pin name.
    /// An empty list when no gate output changes.
    /// </returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <exception cref="ArgumentException">An input bit is missing or unknown.</exception>
    public static IReadOnlyList<LogicSwitchEvent> Compute(
        LogicNetworkEvaluator network,
        IReadOnlyDictionary<string, bool> previousInputs,
        IReadOnlyDictionary<string, bool> nextInputs)
    {
        if (network == null) throw new ArgumentNullException(nameof(network));
        if (previousInputs == null) throw new ArgumentNullException(nameof(previousInputs));
        if (nextInputs == null) throw new ArgumentNullException(nameof(nextInputs));
        network.ValidateInputBits(previousInputs);
        network.ValidateInputBits(nextInputs);

        var before = EvaluateAllGateOutputs(network, previousInputs);
        var after = EvaluateAllGateOutputs(network, nextInputs);

        var switchTimes = new Dictionary<LogicPinRef, double>();
        var events = new List<LogicSwitchEvent>();
        foreach (var gateId in network.EvaluationOrder)
        {
            var gate = network.Gates[gateId];
            var arrival = LatestInputArrival(network, gateId, switchTimes);
            var switchTime = arrival + network.GateDelaysPicoseconds[gateId];
            foreach (var pinName in gate.OutputPinNames)
            {
                var pin = new LogicPinRef(gateId, pinName);
                if (before[pin] == after[pin])
                    continue;
                switchTimes[pin] = switchTime;
                events.Add(new LogicSwitchEvent(switchTime, gateId, pinName, after[pin]));
            }
        }

        return events
            .OrderBy(e => e.TimePicoseconds)
            .ThenBy(e => e.GateId, StringComparer.Ordinal)
            .ThenBy(e => e.OutputPin, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The latest arrival over one gate's inputs: a network input is stable at
    /// its final level from t = 0; a gate-output driver contributes its switch
    /// time plus the wire delay when it switched, and 0 (stable) otherwise.
    /// </summary>
    private static double LatestInputArrival(
        LogicNetworkEvaluator network,
        string gateId,
        IReadOnlyDictionary<LogicPinRef, double> switchTimes)
    {
        var latest = 0.0;
        foreach (var pinName in network.Gates[gateId].InputPinNames)
        {
            var load = new LogicPinRef(gateId, pinName);
            if (network.InputWiring[load] is not LogicNetDriver.GateOutput source)
                continue;
            if (!switchTimes.TryGetValue(source.Pin, out var driverSwitch))
                continue;
            var edge = new LogicWireEdge(source.Pin, load);
            var arrival = driverSwitch + network.WireDelaysPicoseconds[edge];
            if (arrival > latest)
                latest = arrival;
        }
        return latest;
    }

    /// <summary>
    /// Evaluates every gate output pin (not just the network taps) for one input
    /// assignment, walking the same topological order <see cref="LogicNetworkEvaluator.Evaluate"/>
    /// uses.
    /// </summary>
    private static IReadOnlyDictionary<LogicPinRef, bool> EvaluateAllGateOutputs(
        LogicNetworkEvaluator network,
        IReadOnlyDictionary<string, bool> inputBits)
    {
        var outputs = new Dictionary<LogicPinRef, bool>();
        foreach (var gateId in network.EvaluationOrder)
        {
            var gate = network.Gates[gateId];
            var gateInputs = new Dictionary<string, bool>(gate.InputPinNames.Count);
            foreach (var pinName in gate.InputPinNames)
            {
                var load = new LogicPinRef(gateId, pinName);
                gateInputs[pinName] = network.InputWiring[load] switch
                {
                    LogicNetDriver.NetworkInput input => inputBits[input.PinName],
                    LogicNetDriver.GateOutput source => outputs[source.Pin],
                    _ => throw new InvalidOperationException("Unsupported driver type."),
                };
            }
            foreach (var (pinName, bit) in gate.Evaluate(gateInputs))
                outputs[new LogicPinRef(gateId, pinName)] = bit;
        }
        return outputs;
    }
}
