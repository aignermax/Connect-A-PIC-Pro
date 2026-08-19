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
/// <see cref="LogicNetworkEvaluator.WireDelaysPicoseconds"/> (tolerance 1e-9), bounds
/// every event by the critical path's total delay, and lands the last event of a
/// full carry ripple (A=15, B=0, Cin 0→1) exactly on its driving path's edge-by-edge
/// delay — equality, not just ≤. A no-change flip yields an empty timeline.
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
}
