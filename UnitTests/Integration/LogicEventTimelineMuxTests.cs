using CAP_Core.Analysis.LogicAnalysis;
using Shouldly;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Event timeline over the shipped <c>examples/Logic Gate MUX.lun</c> (issue #1059):
/// toggling Sel with A = 0, B = 1 switches the output from A to B and produces a
/// non-empty, strictly time-ordered list of per-gate switch events whose every event
/// references a real gate of the assembled network and whose last event lands at or
/// below the critical-path delay. Mirrors <c>LogicEventTimelineFullAdderTests</c>
/// (#1035) so the future execution visualizer's data structure stays pinned on the
/// datapath steering element too.
/// </summary>
public class LogicEventTimelineMuxTests
    : IClassFixture<LogicGateMuxExampleTests.MuxFixture>
{
    private readonly LogicGateMuxExampleTests.MuxFixture _fixture;

    /// <summary>Attaches the shared loaded MUX network.</summary>
    public LogicEventTimelineMuxTests(LogicGateMuxExampleTests.MuxFixture fixture) =>
        _fixture = fixture;

    [Fact]
    public void Compute_TogglingSel_ProducesOrderedEventsOnRealGates()
    {
        var network = _fixture.Network;

        var events = LogicEventTimeline.Compute(
            network,
            _fixture.InputBits(a: false, b: true, sel: false),
            _fixture.InputBits(a: false, b: true, sel: true));

        events.ShouldNotBeEmpty("Sel steers the output — Out must switch from A to B");
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
            _fixture.InputBits(a: true, b: false, sel: true),
            _fixture.InputBits(a: true, b: false, sel: true));

        events.ShouldBeEmpty("no input changed → no gate output changes → no events");
    }

    [Fact]
    public void Compute_EveryEventMatchesTheEvaluatedAfterState()
    {
        var network = _fixture.Network;
        var before = _fixture.InputBits(a: false, b: true, sel: false);
        var after = _fixture.InputBits(a: false, b: true, sel: true);

        var events = LogicEventTimeline.Compute(network, before, after);
        var afterTaps = network.Evaluate(after);

        foreach (var e in events)
        {
            afterTaps[$"{e.GateId}.{e.OutputPin}"].ShouldBe(e.NewValue,
                $"the event's new value must equal the network's evaluated after-state at {e.GateId}.{e.OutputPin}");
        }
    }
}
