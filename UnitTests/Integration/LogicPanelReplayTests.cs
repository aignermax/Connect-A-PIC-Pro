using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Analysis.LogicAnalysis;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// ViewModel tests for the Logic panel's timeline replay (issue #1058, rung 5
/// visualizer slice 3): on the shipped <c>examples/Logic Gate Full Adder.lun</c>,
/// selecting timeline event k pushes the independently derived state at t_k onto the
/// canvas badges — every pin whose switch event has time ≤ t_k shows its new value,
/// every other pin still shows the before value (mirror derivation in test code over
/// <see cref="LogicNetworkEvaluator.Evaluate"/>, like
/// <c>LogicTimelineCriticalPathConsistencyTests</c>). Prev/Next walk the event bounds,
/// deselecting returns the badges to the live end state, a new toggle exits replay,
/// and a design edit clears replay together with the network.
/// </summary>
public class LogicPanelReplayTests
    : IClassFixture<LogicGateFullAdderExampleTests.FullAdderFixture>
{
    private readonly LogicGateFullAdderExampleTests.FullAdderFixture _fixture;

    /// <summary>Attaches the shared loaded full-adder network.</summary>
    public LogicPanelReplayTests(LogicGateFullAdderExampleTests.FullAdderFixture fixture) =>
        _fixture = fixture;

    [Fact]
    public async Task SelectEvent_FullAdder_BadgesMatchDerivedStateAtThatTime()
    {
        var vm = await BuildAndToggleCin();

        for (var k = 0; k < vm.TimelineEvents.Count; k++)
        {
            vm.SelectTimelineEventCommand.Execute(vm.TimelineEvents[k]);

            vm.IsReplayActive.ShouldBeTrue();
            vm.TimelineEvents[k].IsSelected.ShouldBeTrue($"row {k} is the replayed event");
            vm.TimelineEvents.Count(row => row.IsSelected).ShouldBe(1,
                "exactly one row is highlighted at a time");
            vm.ReplayTimeText.ShouldContain($"{vm.TimelineEvents[k].Event.TimePicoseconds:0.0}");
            BadgesShouldShow(StateAt(vm.TimelineEvents[k].Event.TimePicoseconds),
                $"selecting event {k} must show the state at t_k: switched pins on their new " +
                "value, every other pin still on its before value");
        }
    }

    [Fact]
    public async Task SelectEvent_FullAdder_NamedOutputChipsFreezeAtTheDerivedState()
    {
        // Issue #1067: the full adder's S and Cout taps are signal-named (issue #1046),
        // so their output chips carry the name — replay (#1058) must keep the named
        // chips and freeze them at the derived state of the selected instant.
        var vm = await BuildAndToggleCin();

        vm.SelectTimelineEventCommand.Execute(vm.TimelineEvents[0]);

        var firstTime = vm.TimelineEvents[0].Event.TimePicoseconds;
        var state = StateAt(firstTime);
        var namedOutputs = _fixture.Canvas.LogicGateStates.Badges
            .Where(b => b.HasSignalName && IsOutputChip(b)).ToList();
        namedOutputs.Select(b => b.SignalName).ShouldBe(new[] { "S", "Cout" }, ignoreOrder: true,
            "the signal-named output taps keep their names on the replayed canvas");
        foreach (var chip in namedOutputs)
        {
            chip.IsOne.ShouldBe(state[$"{chip.GroupName}.{chip.PinName}"],
                $"the '{chip.SignalName}' chip freezes at the derived state of t = {firstTime:0.0} ps");
            chip.LabelText.ShouldBe($"{chip.SignalName} = {(chip.IsOne ? "1" : "0")}");
        }

        vm.SelectTimelineEventCommand.Execute(vm.TimelineEvents[0]);

        vm.IsReplayActive.ShouldBeFalse();
        var live = LiveEndState();
        foreach (var chip in _fixture.Canvas.LogicGateStates.Badges.Where(b => b.HasSignalName && IsOutputChip(b)))
        {
            chip.IsOne.ShouldBe(live[$"{chip.GroupName}.{chip.PinName}"],
                "deselecting returns the named output chips to the live end state");
        }
    }

    [Fact]
    public async Task SelectEvent_FullAdder_EveryUnswitchedPinKeepsItsBeforeValue()
    {
        var vm = await BuildAndToggleCin();

        vm.SelectTimelineEventCommand.Execute(vm.TimelineEvents[0]);

        var before = ByGatePin(
            _fixture.Network.Evaluate(_fixture.InputBits(a: false, b: false, cin: false)));
        var firstTime = vm.TimelineEvents[0].Event.TimePicoseconds;
        var switched = vm.TimelineEvents
            .Where(e => e.Event.TimePicoseconds <= firstTime)
            .Select(e => $"{e.Event.GateId}.{e.Event.OutputPin}")
            .ToHashSet();
        foreach (var badge in _fixture.Canvas.LogicGateStates.Badges.Where(IsOutputChip))
        {
            var tap = $"{badge.GroupName}.{badge.PinName}";
            if (!switched.Contains(tap))
                badge.IsOne.ShouldBe(before[tap],
                    $"{tap} has not switched yet at t = {firstTime:0.0} ps — it stays on its old bit");
        }
    }

    [Fact]
    public async Task PrevNext_FullAdder_WalksTheTimelineWithinBounds()
    {
        var vm = await BuildAndToggleCin();
        vm.TimelineEvents.Count.ShouldBeGreaterThan(1, "the Cin ripple must chain through several gates");

        vm.PreviousTimelineEventCommand.CanExecute(null).ShouldBeFalse(
            "nothing is selected — there is no earlier event than the live state");
        vm.NextTimelineEventCommand.CanExecute(null).ShouldBeTrue(
            "from the live state, Next starts the replay at the first event");

        vm.NextTimelineEventCommand.Execute(null);
        vm.SelectedTimelineEvent.ShouldBe(vm.TimelineEvents[0]);
        vm.PreviousTimelineEventCommand.CanExecute(null).ShouldBeFalse("already at the first event");

        for (var k = 1; k < vm.TimelineEvents.Count; k++)
        {
            vm.NextTimelineEventCommand.CanExecute(null).ShouldBeTrue();
            vm.NextTimelineEventCommand.Execute(null);
            vm.SelectedTimelineEvent.ShouldBe(vm.TimelineEvents[k]);
            BadgesShouldShow(StateAt(vm.TimelineEvents[k].Event.TimePicoseconds),
                $"stepping to event {k} must advance the canvas to that instant");
        }
        vm.NextTimelineEventCommand.CanExecute(null).ShouldBeFalse("already at the last event");

        vm.PreviousTimelineEventCommand.Execute(null);
        vm.SelectedTimelineEvent.ShouldBe(vm.TimelineEvents[^2],
            "Prev steps one event earlier");
    }

    [Fact]
    public async Task Deselect_FullAdder_ReturnsBadgesToTheLiveEndState()
    {
        var vm = await BuildAndToggleCin();
        vm.SelectTimelineEventCommand.Execute(vm.TimelineEvents[0]);
        var live = LiveEndState();
        BadgeStates().Count(pair => live[pair.Key] != pair.Value).ShouldBeGreaterThan(0,
            "the replayed first instant must differ from the settled end state");

        vm.SelectTimelineEventCommand.Execute(vm.TimelineEvents[0]);

        vm.IsReplayActive.ShouldBeFalse("clicking the selected row again deselects it");
        vm.SelectedTimelineEvent.ShouldBeNull();
        vm.ReplayTimeText.ShouldBeEmpty();
        vm.TimelineEvents.ShouldAllBe(row => !row.IsSelected, "no row stays highlighted");
        BadgesShouldShow(live, "deselecting returns the badges to the live end state");
    }

    [Fact]
    public async Task ExitReplay_FullAdder_ReturnsBadgesToTheLiveEndState()
    {
        var vm = await BuildAndToggleCin();
        vm.SelectTimelineEventCommand.Execute(vm.TimelineEvents[^1]);

        vm.ExitReplayCommand.Execute(null);

        vm.IsReplayActive.ShouldBeFalse();
        BadgesShouldShow(LiveEndState(),
            "the 'back to live' button restores the settled end state");
    }

    [Fact]
    public async Task NewToggle_FullAdder_ExitsReplayAndShowsTheNewLiveState()
    {
        var vm = await BuildAndToggleCin();
        vm.SelectTimelineEventCommand.Execute(vm.TimelineEvents[0]);
        vm.IsReplayActive.ShouldBeTrue();

        vm.Inputs.Single(i => i.PinName == "Cin").IsOn = false;

        vm.IsReplayActive.ShouldBeFalse("a new toggle exits replay");
        vm.SelectedTimelineEvent.ShouldBeNull();
        vm.ReplayTimeText.ShouldBeEmpty();
        BadgesShouldShow(
            ByGatePin(_fixture.Network.Evaluate(_fixture.InputBits(a: false, b: false, cin: false))),
            "the badges follow the new toggle's live end state, not the stale replay");
    }

    [Fact]
    public async Task DesignEdit_FullAdder_ClearsReplayTogetherWithTheNetwork()
    {
        var vm = await BuildAndToggleCin();
        vm.SelectTimelineEventCommand.Execute(vm.TimelineEvents[0]);
        vm.IsReplayActive.ShouldBeTrue();

        // Add and remove a probe component, restoring the shared fixture canvas for the
        // other tests; the add alone must discard network, timeline, and replay.
        var probe = new ComponentViewModel(TestComponentFactory.CreateStraightWaveGuide());
        _fixture.Canvas.Components.Add(probe);
        try
        {
            vm.HasNetwork.ShouldBeFalse();
            vm.IsReplayActive.ShouldBeFalse("a design edit ends replay together with the network");
            vm.SelectedTimelineEvent.ShouldBeNull();
            vm.TimelineEvents.ShouldBeEmpty();
            _fixture.Canvas.LogicGateStates.Badges.ShouldBeEmpty(
                "the badges of a discarded network — replayed or live — must vanish");
        }
        finally
        {
            _fixture.Canvas.Components.Remove(probe);
        }
    }

    /// <summary>Builds the network on the fixture canvas and toggles Cin on, producing the timeline.</summary>
    private async Task<LogicPanelViewModel> BuildAndToggleCin()
    {
        var vm = new LogicPanelViewModel();
        vm.Configure(_fixture.Canvas);
        await vm.BuildNetworkCommand.ExecuteAsync(null);
        vm.HasNetwork.ShouldBeTrue(vm.StatusText);
        vm.Inputs.Single(i => i.PinName == "Cin").IsOn = true;
        vm.TimelineEvents.ShouldNotBeEmpty("toggling Cin must produce switch events");
        return vm;
    }

    /// <summary>
    /// The output-pin badges currently on the canvas, keyed <c>gate.pin</c>.
    /// Named input chips (issue #1051) ride along on the overlay but carry the live
    /// input bits, not gate output states — replay derivations exclude them. A named
    /// output chip (issue #1067) still carries a gate output state, so it stays in.
    /// </summary>
    private Dictionary<string, bool> BadgeStates() =>
        _fixture.Canvas.LogicGateStates.Badges
            .Where(IsOutputChip)
            .ToDictionary(b => $"{b.GroupName}.{b.PinName}", b => b.IsOne);

    /// <summary>True for a gate's output-pin chip — named when its tap carries a persisted
    /// signal name (issue #1067), anonymous elsewhere.</summary>
    private bool IsOutputChip(LogicGateBadgeViewModel badge) =>
        _fixture.Network.OutputTaps.Values.Contains(new LogicPinRef(badge.GroupName, badge.PinName));

    /// <summary>Asserts the canvas badges show exactly the expected pin states.</summary>
    private void BadgesShouldShow(IReadOnlyDictionary<string, bool> expected, string because)
    {
        var actual = BadgeStates();
        actual.Count.ShouldBe(expected.Count, because);
        foreach (var (tap, bit) in expected)
            actual[tap].ShouldBe(bit, $"{tap}: {because}");
    }

    /// <summary>The settled end state after the Cin toggle the tests perform.</summary>
    private IReadOnlyDictionary<string, bool> LiveEndState() =>
        ByGatePin(_fixture.Network.Evaluate(_fixture.InputBits(a: false, b: false, cin: true)));

    /// <summary>
    /// Re-keys a tap-keyed evaluation result by raw <c>gate.pin</c>: since issue #1046
    /// a signal-named output evaluates under its signal name (the adder's sum reads
    /// <c>S</c>), while the canvas badges always carry gate and pin.
    /// </summary>
    private Dictionary<string, bool> ByGatePin(IReadOnlyDictionary<string, bool> tapKeyed) =>
        _fixture.Network.OutputTaps.ToDictionary(
            tap => $"{tap.Value.GateId}.{tap.Value.PinName}", tap => tapKeyed[tap.Key]);

    /// <summary>
    /// The independently derived state at time t: evaluate every gate output for the
    /// before-toggle inputs, then apply the new value of every switch event whose time
    /// is ≤ t — the rule of issue #1058 re-derived in test code without touching the
    /// ViewModel's replay path.
    /// </summary>
    private Dictionary<string, bool> StateAt(double timePicoseconds)
    {
        var state = ByGatePin(
            _fixture.Network.Evaluate(_fixture.InputBits(a: false, b: false, cin: false)));
        var events = LogicEventTimeline.Compute(
            _fixture.Network,
            _fixture.InputBits(a: false, b: false, cin: false),
            _fixture.InputBits(a: false, b: false, cin: true));
        foreach (var e in events)
        {
            if (e.TimePicoseconds > timePicoseconds)
                break;
            state[$"{e.GateId}.{e.OutputPin}"] = e.NewValue;
        }
        return state;
    }
}
