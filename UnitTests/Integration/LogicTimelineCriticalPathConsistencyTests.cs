using CAP_Core.Analysis.LogicAnalysis;
using Shouldly;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// E2E honesty pinning the event timeline (#1035/#1041) against the critical path
/// (#1002/#1004, wire delays #1020/#1027) on the shipped 344-gate 4-bit adder
/// (issue #1047): both panels compute timing through different code paths over the
/// same delay maps, so this recomputes every event's arrival independently from
/// <see cref="LogicNetworkEvaluator.GateDelaysPicoseconds"/> and
/// <see cref="LogicNetworkEvaluator.WireDelaysPicoseconds"/> (tolerance 1e-9), pins
/// the whole switch set against a from-scratch mirror propagation (no missing or
/// extra events), bounds every event by the critical path's total delay, and lands
/// the last event of a full carry ripple (A=15, B=0, Cin 0→1) exactly on its driving
/// path's edge-by-edge delay — equality, not just ≤. A no-change flip yields an
/// empty timeline.
/// </summary>
public class LogicTimelineCriticalPathConsistencyTests
    : IClassFixture<LogicGateFourBitAdderExampleTests.FourBitAdderFixture>
{
    private const double Tolerance = 1e-9;

    private readonly LogicGateFourBitAdderExampleTests.FourBitAdderFixture _fixture;

    /// <summary>Attaches the shared 4-bit-adder fixture pinned in #1031.</summary>
    public LogicTimelineCriticalPathConsistencyTests(
        LogicGateFourBitAdderExampleTests.FourBitAdderFixture fixture) =>
        _fixture = fixture;

    [Fact]
    public void FullRippleFlip_EventsTimeOrdered_EveryArrivalRecomputedFromDelayMaps()
    {
        var events = FullRippleEvents();
        AssertFullRippleSensitized(events);

        events.ShouldBe(events
                .OrderBy(e => e.TimePicoseconds)
                .ThenBy(e => e.GateId, StringComparer.Ordinal)
                .ThenBy(e => e.OutputPin, StringComparer.Ordinal)
                .ToList(),
            "events are strictly time-ordered, ties broken by gate id then pin name");

        var switchTimes = PinSwitchTimes(events);
        foreach (var evt in events)
            evt.TimePicoseconds.ShouldBe(ExpectedSwitchTime(evt, switchTimes), Tolerance,
                $"event {evt.GateId}.{evt.OutputPin}: arrival + gate delay recomputed from " +
                "GateDelaysPicoseconds/WireDelaysPicoseconds over the input wiring must match");
    }

    [Fact]
    public void FullRippleFlip_NoEventArrivesLaterThanTheCriticalPath()
    {
        var critical = _fixture.Network.CriticalPathDelayPicoseconds;
        critical.ShouldBeGreaterThan(0, "non-vacuous bound: the critical path has a real delay");
        foreach (var evt in FullRippleEvents())
            evt.TimePicoseconds.ShouldBeLessThanOrEqualTo(critical + Tolerance,
                $"event {evt.GateId}.{evt.OutputPin} must respect the worst-case path");
    }

    [Fact]
    public void FullRippleFlip_LastEventEqualsItsDrivingPathRecomputedEdgeByEdge()
    {
        var events = FullRippleEvents();
        var switchTimes = PinSwitchTimes(events);
        var last = events[^1];
        var (total, chainLength) = DrivingPathDelay(last, switchTimes);
        chainLength.ShouldBeGreaterThan(1, "the full ripple must chain through several gates");
        last.TimePicoseconds.ShouldBe(total, Tolerance,
            "the last event lands exactly on its driving path, summed edge-by-edge");
    }

    [Fact]
    public void FullRippleFlip_TimelineReportsExactlyTheIndependentlyDerivedSwitchSet()
    {
        var before = _fixture.InputBits(15, 0, false);
        var after = _fixture.InputBits(15, 0, true);
        var mirror = MirrorPropagate(before, after);
        var events = FullRippleEvents();

        // Per-event recomputation cannot catch a missing or extra event — the mirror
        // derives the whole switch set without looking at the timeline.
        events.Count.ShouldBe(mirror.SwitchTimes.Count,
            "the timeline reports exactly the independently derived switch set — no missing or extra events");
        foreach (var evt in events)
        {
            var pin = new LogicPinRef(evt.GateId, evt.OutputPin);
            mirror.SwitchTimes.TryGetValue(pin, out var expected).ShouldBeTrue(
                $"the independent propagation also switches {evt.GateId}.{evt.OutputPin}");
            evt.TimePicoseconds.ShouldBe(expected, Tolerance,
                $"the mirror arrival of {evt.GateId}.{evt.OutputPin} must match");
            evt.NewValue.ShouldBe(mirror.AfterValues[pin],
                $"the event's new value for {evt.GateId}.{evt.OutputPin} must match the settled level");
        }
    }

    [Fact]
    public void NoChangeFlip_YieldsEmptyTimeline_EveryRealToggleSensitizes()
    {
        var before = _fixture.InputBits(15, 0, false);
        LogicEventTimeline.Compute(_fixture.Network, before, before).ShouldBeEmpty(
            "an unchanged assignment flips no gate output");

        // Every operand bit feeds its stage's XOR ladder, which flips at least one gate
        // output on any toggle, so no "cannot affect any output" input exists here —
        // the empty-timeline contract is pinned on the degenerate no-change flip above.
        foreach (var input in _fixture.Network.InputPinNames)
        {
            var flipped = new Dictionary<string, bool>(before) { [input] = !before[input] };
            LogicEventTimeline.Compute(_fixture.Network, before, flipped).ShouldNotBeEmpty(
                $"toggling '{input}' must flip at least one gate");
        }
    }

    /// <summary>A=15, B=0, Cin 0→1: the longest carry ripple — every stage's carry and sum flips.</summary>
    private IReadOnlyList<LogicSwitchEvent> FullRippleEvents() =>
        LogicEventTimeline.Compute(
            _fixture.Network,
            _fixture.InputBits(15, 0, false),
            _fixture.InputBits(15, 0, true));

    /// <summary>The ripple must actually flip every stage's sum bit and the final carry.</summary>
    private static void AssertFullRippleSensitized(IReadOnlyList<LogicSwitchEvent> events)
    {
        events.ShouldNotBeEmpty("the Cin 0→1 ripple must flip gates");
        for (var stage = 0; stage < 4; stage++)
            events.ShouldContain(e => e.GateId == $"T{stage}H2SUM", $"stage {stage}'s sum bit must flip");
        events.ShouldContain(e => e.GateId == "T3OROUT", "the final Cout must flip");
    }

    /// <summary>Event times keyed by pin: a gate's arrival looks up its drivers here.</summary>
    private static Dictionary<LogicPinRef, double> PinSwitchTimes(IReadOnlyList<LogicSwitchEvent> events) =>
        events.ToDictionary(e => new LogicPinRef(e.GateId, e.OutputPin), e => e.TimePicoseconds);

    /// <summary>One event's expected time: slowest switching-driver arrival plus gate delay.</summary>
    private double ExpectedSwitchTime(LogicSwitchEvent evt, IReadOnlyDictionary<LogicPinRef, double> switchTimes) =>
        BestArrival(evt.GateId, switchTimes).Arrival
        + _fixture.Network.GateDelaysPicoseconds[evt.GateId];

    /// <summary>
    /// The slowest arrival over one gate's inputs and the wire edge it arrived on: a
    /// gate-output driver contributes its recorded switch time plus that wire's delay.
    /// </summary>
    private (double Arrival, LogicWireEdge? Edge) BestArrival(
        string gateId, IReadOnlyDictionary<LogicPinRef, double> switchTimes)
    {
        var arrival = 0.0;
        LogicWireEdge? edge = null;
        foreach (var pinName in _fixture.Network.Gates[gateId].InputPinNames)
        {
            var load = new LogicPinRef(gateId, pinName);
            if (_fixture.Network.InputWiring[load] is not LogicNetDriver.GateOutput source)
                continue;
            if (!switchTimes.TryGetValue(source.Pin, out var driverSwitch))
                continue;
            var wireEdge = new LogicWireEdge(source.Pin, load);
            var candidate = driverSwitch + _fixture.Network.WireDelaysPicoseconds[wireEdge];
            if (edge == null || candidate > arrival)
            {
                arrival = candidate;
                edge = wireEdge;
            }
        }
        return (arrival, edge);
    }

    /// <summary>
    /// The last event's driving path, walked back gate-by-gate to a gate driven only by
    /// network inputs, summing gate delays and wire delays edge-by-edge.
    /// </summary>
    private (double Total, int ChainLength) DrivingPathDelay(
        LogicSwitchEvent last, IReadOnlyDictionary<LogicPinRef, double> switchTimes)
    {
        var total = 0.0;
        var chainLength = 0;
        var gateId = last.GateId;
        while (true)
        {
            total += _fixture.Network.GateDelaysPicoseconds[gateId];
            chainLength++;
            var (_, edge) = BestArrival(gateId, switchTimes);
            if (edge == null)
                break;
            total += _fixture.Network.WireDelaysPicoseconds[edge];
            gateId = edge.Source.GateId;
        }
        return (total, chainLength);
    }

    /// <summary>
    /// Re-derives the whole switch propagation in test code without looking at the timeline:
    /// evaluate every gate output before and after, then walk the gates in topological order
    /// adding the slowest switching-driver arrival plus the gate delay to every changed output.
    /// </summary>
    private MirrorPropagation MirrorPropagate(
        IReadOnlyDictionary<string, bool> before, IReadOnlyDictionary<string, bool> after)
    {
        var beforeValues = EvaluateAllGateOutputs(before);
        var afterValues = EvaluateAllGateOutputs(after);
        var switchTimes = new Dictionary<LogicPinRef, double>();
        foreach (var gateId in _fixture.Network.EvaluationOrder)
        {
            var arrival = 0.0;
            foreach (var pinName in _fixture.Network.Gates[gateId].InputPinNames)
            {
                var load = new LogicPinRef(gateId, pinName);
                if (_fixture.Network.InputWiring[load] is not LogicNetDriver.GateOutput source)
                    continue;
                if (!switchTimes.TryGetValue(source.Pin, out var driverSwitch))
                    continue;
                var candidate = driverSwitch + _fixture.Network.WireDelaysPicoseconds[new LogicWireEdge(source.Pin, load)];
                if (candidate > arrival)
                    arrival = candidate;
            }
            var switchTime = arrival + _fixture.Network.GateDelaysPicoseconds[gateId];
            foreach (var pinName in _fixture.Network.Gates[gateId].OutputPinNames)
            {
                var pin = new LogicPinRef(gateId, pinName);
                if (beforeValues[pin] != afterValues[pin]) switchTimes[pin] = switchTime;
            }
        }
        return new MirrorPropagation(switchTimes, afterValues);
    }

    /// <summary>Every gate output pin's level for one input assignment, in topological order.</summary>
    private IReadOnlyDictionary<LogicPinRef, bool> EvaluateAllGateOutputs(
        IReadOnlyDictionary<string, bool> inputBits)
    {
        var outputs = new Dictionary<LogicPinRef, bool>();
        foreach (var gateId in _fixture.Network.EvaluationOrder)
        {
            var gate = _fixture.Network.Gates[gateId];
            var gateInputs = new Dictionary<string, bool>(gate.InputPinNames.Count);
            foreach (var pinName in gate.InputPinNames)
            {
                var load = new LogicPinRef(gateId, pinName);
                gateInputs[pinName] = _fixture.Network.InputWiring[load] switch
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

    /// <summary>The test-side re-derivation: switch times per pin plus the settled after-levels.</summary>
    private sealed record MirrorPropagation(
        Dictionary<LogicPinRef, double> SwitchTimes,
        IReadOnlyDictionary<LogicPinRef, bool> AfterValues);
}
