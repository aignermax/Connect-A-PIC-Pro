using CAP_Core.Analysis.LogicAnalysis;
using Shouldly;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Event timeline over the shipped <c>examples/Logic Gate Full Adder.lun</c>
/// (issue #1035, rung 4→5 groundwork): toggling Cin with A = B = 0 produces a
/// non-empty, strictly time-ordered list of per-gate switch events whose every
/// event references a real gate of the assembled network and whose last event
/// lands at or below the critical-path delay. Together with the hand-built
/// networks of <c>LogicEventTimelineTests</c> this pins the data structure any
/// future execution visualizer consumes.
/// </summary>
public class LogicEventTimelineFullAdderTests
    : IClassFixture<LogicGateFullAdderExampleTests.FullAdderFixture>
{
    private readonly LogicGateFullAdderExampleTests.FullAdderFixture _fixture;

    /// <summary>Attaches the shared loaded full-adder network.</summary>
    public LogicEventTimelineFullAdderTests(LogicGateFullAdderExampleTests.FullAdderFixture fixture) =>
        _fixture = fixture;

    [Fact]
    public void Compute_TogglingCinWithAAndBLow_ProducesOrderedEventsOnRealGates()
    {
        var network = _fixture.Network;

        var events = LogicEventTimeline.Compute(
            network,
            _fixture.InputBits(a: false, b: false, cin: false),
            _fixture.InputBits(a: false, b: false, cin: true));

        events.ShouldNotBeEmpty("Cin feeds the second half adder — Sum must switch");
        events.ShouldAllBe(
            e => network.Gates.ContainsKey(e.GateId),
            "every event references a real gate of the assembled network");
        events.ShouldAllBe(
            e => network.Gates[e.GateId].OutputPinNames.Contains(e.OutputPin),
            "every event references a real output pin of its gate");
        events.Select(e => e.TimePicoseconds).ShouldBe(
            events.Select(e => e.TimePicoseconds).OrderBy(t => t),
            "the timeline is time-ordered");
        events.ShouldAllBe(
            e => e.TimePicoseconds >= 0 && double.IsFinite(e.TimePicoseconds),
            "every switch time is a finite, non-negative number of picoseconds");
        events[^1].TimePicoseconds.ShouldBeLessThanOrEqualTo(
            network.CriticalPathDelayPicoseconds,
            "no signal can arrive later than the critical path");
    }

    [Fact]
    public void Compute_IdenticalInputAssignment_ProducesEmptyTimeline()
    {
        var events = LogicEventTimeline.Compute(
            _fixture.Network,
            _fixture.InputBits(a: true, b: false, cin: true),
            _fixture.InputBits(a: true, b: false, cin: true));

        events.ShouldBeEmpty("no input changed → no gate output changes → no events");
    }

    [Fact]
    public void Compute_EveryEventMatchesTheEvaluatedAfterState()
    {
        var network = _fixture.Network;
        var before = _fixture.InputBits(a: false, b: false, cin: false);
        var after = _fixture.InputBits(a: false, b: false, cin: true);

        var events = LogicEventTimeline.Compute(network, before, after);
        var afterTaps = network.Evaluate(after);
        var tapNameByPin = network.OutputTaps.ToDictionary(tap => tap.Value, tap => tap.Key);

        foreach (var e in events)
        {
            var pin = new LogicPinRef(e.GateId, e.OutputPin);
            afterTaps[tapNameByPin[pin]].ShouldBe(e.NewValue,
                $"the event's new value must equal the network's evaluated after-state at {e.GateId}.{e.OutputPin}");
        }
    }
}
