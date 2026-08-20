using CAP_Core.Analysis.LogicAnalysis;
using Shouldly;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Event timeline over the shipped <c>examples/Logic Gate ALU 1-bit.lun</c> (issue #1070):
/// toggling Op with A = 0, B = 1 switches the ALU output from AND(A, B) = 0 to
/// OR(A, B) = 1 and produces a non-empty, strictly time-ordered list of per-gate
/// switch events whose every event references a real gate of the assembled network and
/// whose last event lands at or below the critical-path delay. Mirrors
/// <c>LogicEventTimelineMuxTests</c> (#1059) so the future execution visualizer's data
/// structure stays pinned on the datapath-steering ALU too. The after-state check
/// resolves each event through the network's output taps because the final gate's tap
/// carries the output signal name <c>Result</c> (#1046), not the raw <c>Out.Y</c> id.
/// </summary>
public class LogicEventTimelineAluTests
    : IClassFixture<LogicGateAluExampleTests.AluFixture>
{
    private readonly LogicGateAluExampleTests.AluFixture _fixture;

    /// <summary>Attaches the shared loaded ALU network.</summary>
    public LogicEventTimelineAluTests(LogicGateAluExampleTests.AluFixture fixture) =>
        _fixture = fixture;

    [Fact]
    public void Compute_TogglingOp_ProducesOrderedEventsOnRealGates()
    {
        var network = _fixture.Network;

        var events = LogicEventTimeline.Compute(
            network,
            _fixture.InputBits(a: false, b: true, op: false),
            _fixture.InputBits(a: false, b: true, op: true));

        events.ShouldNotBeEmpty("Op steers the datapath — Result must switch from AND(A, B) = 0 to OR(A, B) = 1");
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
            _fixture.InputBits(a: true, b: false, op: true),
            _fixture.InputBits(a: true, b: false, op: true));

        events.ShouldBeEmpty("no input changed → no gate output changes → no events");
    }

    [Fact]
    public void Compute_EveryEventMatchesTheEvaluatedAfterState()
    {
        var network = _fixture.Network;
        var before = _fixture.InputBits(a: false, b: true, op: false);
        var after = _fixture.InputBits(a: false, b: true, op: true);

        var events = LogicEventTimeline.Compute(network, before, after);
        var afterTaps = network.Evaluate(after);
        var tapNameByPin = network.OutputTaps.ToDictionary(tap => tap.Value, tap => tap.Key);

        foreach (var e in events)
        {
            var tapName = tapNameByPin[new LogicPinRef(e.GateId, e.OutputPin)];
            afterTaps[tapName].ShouldBe(e.NewValue,
                $"the event's new value must equal the network's evaluated after-state at {tapName}");
        }
    }
}
