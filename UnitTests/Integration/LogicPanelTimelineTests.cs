using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CAP_Core.Analysis.LogicAnalysis;
using Shouldly;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// ViewModel tests for the Logic panel's event timeline (issue #1045, rung 5
/// visualizer slice 2): the shipped <c>examples/Logic Gate Full Adder.lun</c>
/// loads through the real load path, the Build command assembles its logic
/// network, and toggling an input populates the Timeline section with the
/// switch events of that toggle — the same events, in the same order, that
/// <see cref="LogicEventTimeline.Compute"/> produces. Before the first toggle
/// the section shows the empty state; every event row carries the gate id,
/// the output pin, the arrival time, and the 0→1 / 1→0 transition.
/// </summary>
public class LogicPanelTimelineTests
    : IClassFixture<LogicGateFullAdderExampleTests.FullAdderFixture>
{
    private readonly LogicGateFullAdderExampleTests.FullAdderFixture _fixture;

    /// <summary>Attaches the shared loaded full-adder network.</summary>
    public LogicPanelTimelineTests(LogicGateFullAdderExampleTests.FullAdderFixture fixture) =>
        _fixture = fixture;

    [Fact]
    public async Task BuildNetwork_FullAdder_BeforeAnyToggle_TimelineIsEmpty()
    {
        var vm = new LogicPanelViewModel();
        vm.Configure(_fixture.Canvas);

        await vm.BuildNetworkCommand.ExecuteAsync(null);

        vm.HasNetwork.ShouldBeTrue(vm.StatusText);
        vm.TimelineEvents.ShouldBeEmpty(
            "no input has toggled yet — the timeline shows the empty state");
        vm.HasTimelineEvents.ShouldBeFalse();
    }

    [Fact]
    public async Task ToggleCin_FullAdder_TimelineMatchesLogicEventTimelineExactly()
    {
        var vm = new LogicPanelViewModel();
        vm.Configure(_fixture.Canvas);
        await vm.BuildNetworkCommand.ExecuteAsync(null);
        vm.HasNetwork.ShouldBeTrue(vm.StatusText);

        vm.Inputs.Single(i => i.PinName == "Cin").IsOn = true;

        var expected = LogicEventTimeline.Compute(
            _fixture.Network,
            _fixture.InputBits(a: false, b: false, cin: false),
            _fixture.InputBits(a: false, b: false, cin: true));

        vm.TimelineEvents.Count.ShouldBe(expected.Count,
            "the panel shows exactly the events LogicEventTimeline.Compute produces");
        for (var i = 0; i < expected.Count; i++)
        {
            var row = vm.TimelineEvents[i];
            var e = expected[i];
            row.Event.TimePicoseconds.ShouldBe(e.TimePicoseconds,
                $"row {i}: time must match the computed event");
            row.Event.GateId.ShouldBe(e.GateId, $"row {i}: gate id must match");
            row.Event.OutputPin.ShouldBe(e.OutputPin, $"row {i}: output pin must match");
            row.Event.NewValue.ShouldBe(e.NewValue, $"row {i}: transition direction must match");
            row.GatePinText.ShouldBe($"{e.GateId}.{e.OutputPin}");
            row.IsRising.ShouldBe(e.NewValue);
        }
        vm.HasTimelineEvents.ShouldBeTrue();
    }

    [Fact]
    public async Task ToggleCin_FullAdder_TimelineRowsAreTimeOrdered()
    {
        var vm = new LogicPanelViewModel();
        vm.Configure(_fixture.Canvas);
        await vm.BuildNetworkCommand.ExecuteAsync(null);

        vm.Inputs.Single(i => i.PinName == "Cin").IsOn = true;

        var times = vm.TimelineEvents.Select(e => e.Event.TimePicoseconds).ToList();
        times.ShouldBe(times.OrderBy(t => t).ToList(),
            "the timeline lists the switch events in arrival-time order");
    }

    [Fact]
    public async Task ToggleCinBackAndForth_FullAdder_EachToggleReplacesTheTimeline()
    {
        var vm = new LogicPanelViewModel();
        vm.Configure(_fixture.Canvas);
        await vm.BuildNetworkCommand.ExecuteAsync(null);

        vm.Inputs.Single(i => i.PinName == "Cin").IsOn = true;
        var riseCount = vm.TimelineEvents.Count;
        riseCount.ShouldBeGreaterThan(0, "toggling Cin on must produce switch events");

        vm.Inputs.Single(i => i.PinName == "Cin").IsOn = false;

        var fallExpected = LogicEventTimeline.Compute(
            _fixture.Network,
            _fixture.InputBits(a: false, b: false, cin: true),
            _fixture.InputBits(a: false, b: false, cin: false));
        vm.TimelineEvents.Count.ShouldBe(fallExpected.Count,
            "toggling Cin back off replaces the timeline with that toggle's events");
        vm.TimelineEvents.Select(e => e.Event).ShouldBe(fallExpected,
            "the replaced timeline matches the computed falling-toggle events exactly");
    }

    [Fact]
    public async Task ToggleInput_FullAdder_RowsShowTimeGatePinAndTransition()
    {
        var vm = new LogicPanelViewModel();
        vm.Configure(_fixture.Canvas);
        await vm.BuildNetworkCommand.ExecuteAsync(null);

        vm.Inputs.Single(i => i.PinName == "Cin").IsOn = true;

        vm.TimelineEvents.ShouldAllBe(
            e => e.TimeText.EndsWith("ps") && e.TimeText.Length > 3,
            "every row shows its arrival time in picoseconds");
        vm.TimelineEvents.ShouldAllBe(
            e => e.GatePinText.Contains('.'),
            "every row names its gate and pin in <gate>.<pin> form");
        vm.TimelineEvents.ShouldAllBe(
            e => e.TransitionText == "0→1" || e.TransitionText == "1→0",
            "every row shows a 0→1 or 1→0 transition");
        vm.TimelineEvents.ShouldContain(
            e => e.IsRising,
            "toggling Cin on from the all-off state produces at least one rising event");
    }

    [Fact]
    public async Task ToggleCin_FullAdder_LastEventDoesNotExceedCriticalPath()
    {
        var vm = new LogicPanelViewModel();
        vm.Configure(_fixture.Canvas);
        await vm.BuildNetworkCommand.ExecuteAsync(null);

        vm.Inputs.Single(i => i.PinName == "Cin").IsOn = true;

        vm.TimelineEvents[^1].Event.TimePicoseconds.ShouldBeLessThanOrEqualTo(
            _fixture.Network.CriticalPathDelayPicoseconds,
            "no signal can arrive later than the critical path — " +
            "the help flyout links the last event to that number");
    }
}
