using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Analysis.LogicAnalysis;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// ViewModel tests for the Logic panel's timeline auto-play (issue #1069, rung 5
/// visualizer slice 4) on the shipped <c>examples/Logic Gate Full Adder.lun</c>.
/// Play selects the first event (or continues from a manual selection), every
/// synchronous <see cref="LogicPanelViewModel.AdvancePlaybackTick"/> advances one
/// event with the canvas badges equal to the independently derived state at t_k
/// (same mirror as <c>LogicPanelReplayTests</c>), the tick after the last event
/// returns to live, Pause freezes, and every manual interaction — (de)selecting a
/// row, stepping, exiting, a new toggle, a design edit — stops playback. The VM is
/// timer-free; the view wires the ticks to a DispatcherTimer.
/// </summary>
public class LogicPanelPlaybackTests
    : IClassFixture<LogicGateFullAdderExampleTests.FullAdderFixture>
{
    private readonly LogicGateFullAdderExampleTests.FullAdderFixture _fixture;

    /// <summary>Attaches the shared loaded full-adder network.</summary>
    public LogicPanelPlaybackTests(LogicGateFullAdderExampleTests.FullAdderFixture fixture) =>
        _fixture = fixture;

    [Fact]
    public async Task Play_FromLive_SelectsTheFirstEvent()
    {
        var vm = await BuildAndToggleCin();

        vm.TogglePlaybackCommand.Execute(null);

        vm.IsPlaying.ShouldBeTrue();
        vm.SelectedTimelineEvent.ShouldBe(vm.TimelineEvents[0],
            "Play from the live state starts the ripple at the first switch event");
        vm.PlayPauseText.ShouldNotBeNullOrEmpty(
            "the button has a label while playing; the exact text is render-tested");
        BadgesShouldShow(StateAt(vm.TimelineEvents[0].Event.TimePicoseconds),
            "pressing Play already shows the state at the first instant");
    }

    [Fact]
    public async Task Tick_FullAdder_BadgesMatchDerivedStateAtEachStep()
    {
        var vm = await BuildAndToggleCin();
        vm.TogglePlaybackCommand.Execute(null);

        for (var k = 1; k < vm.TimelineEvents.Count; k++)
        {
            vm.AdvancePlaybackTick();

            vm.IsPlaying.ShouldBeTrue($"playback holds until the tick after the last event (now at {k})");
            vm.SelectedTimelineEvent.ShouldBe(vm.TimelineEvents[k], $"tick {k} advances exactly one event");
            BadgesShouldShow(StateAt(vm.TimelineEvents[k].Event.TimePicoseconds),
                $"during playback the badges at tick {k} must equal the derived state at t_k");
        }
    }

    [Fact]
    public async Task Tick_AfterLastEvent_EndsPlaybackBackInLiveState()
    {
        var vm = await BuildAndToggleCin();
        vm.TogglePlaybackCommand.Execute(null);
        for (var k = 1; k < vm.TimelineEvents.Count; k++)
            vm.AdvancePlaybackTick();

        vm.AdvancePlaybackTick();

        vm.IsPlaying.ShouldBeFalse("the tick after the last event stops playback");
        vm.IsReplayActive.ShouldBeFalse();
        vm.SelectedTimelineEvent.ShouldBeNull();
        BadgesShouldShow(LiveEndState(), "playback ends in the settled live end state");
    }

    [Fact]
    public async Task Pause_FullAdder_HoldsTheReplayedInstant()
    {
        var vm = await BuildAndToggleCin();
        vm.TogglePlaybackCommand.Execute(null);
        vm.AdvancePlaybackTick();
        var held = vm.SelectedTimelineEvent;
        var heldState = StateAt(held!.Event.TimePicoseconds);

        vm.TogglePlaybackCommand.Execute(null);

        vm.IsPlaying.ShouldBeFalse("toggling while playing pauses");
        vm.SelectedTimelineEvent.ShouldBe(held, "Pause freezes the ripple mid-way");
        BadgesShouldShow(heldState, "the badges stay at the paused instant");
        vm.AdvancePlaybackTick();
        vm.SelectedTimelineEvent.ShouldBe(held, "ticks without playback are no-ops");
    }

    [Fact]
    public async Task Play_FromManualSelection_ContinuesFromThere()
    {
        var vm = await BuildAndToggleCin();
        vm.SelectTimelineEventCommand.Execute(vm.TimelineEvents[1]);

        vm.TogglePlaybackCommand.Execute(null);

        vm.IsPlaying.ShouldBeTrue();
        vm.SelectedTimelineEvent.ShouldBe(vm.TimelineEvents[1],
            "Play does not rewind a manual selection back to the first event");
        vm.AdvancePlaybackTick();
        vm.IsPlaying.ShouldBeFalse("the tick after the last event ends playback");
        vm.SelectedTimelineEvent.ShouldBeNull();
    }

    [Fact]
    public async Task ManualSelect_FullAdder_StopsPlayback()
    {
        var vm = await BuildAndToggleCin();
        vm.TogglePlaybackCommand.Execute(null);

        vm.SelectTimelineEventCommand.Execute(vm.TimelineEvents[0]);

        vm.IsPlaying.ShouldBeFalse("clicking a row takes over from the auto-play");
        vm.SelectedTimelineEvent.ShouldBeNull("clicking the replayed row deselects it");
    }

    [Fact]
    public async Task ManualStep_FullAdder_StopsPlayback()
    {
        var vm = await BuildAndToggleCin();
        vm.TogglePlaybackCommand.Execute(null);

        vm.NextTimelineEventCommand.Execute(null);

        vm.IsPlaying.ShouldBeFalse("manual stepping takes over from the auto-play");
        vm.SelectedTimelineEvent.ShouldBe(vm.TimelineEvents[1], "the step itself still lands");
    }

    [Fact]
    public async Task ExitReplay_FullAdder_StopsPlayback()
    {
        var vm = await BuildAndToggleCin();
        vm.TogglePlaybackCommand.Execute(null);

        vm.ExitReplayCommand.Execute(null);

        vm.IsPlaying.ShouldBeFalse();
        BadgesShouldShow(LiveEndState(), "'back to live' ends playback together with the replay");
    }

    [Fact]
    public async Task NewToggle_FullAdder_StopsPlayback()
    {
        var vm = await BuildAndToggleCin();
        vm.TogglePlaybackCommand.Execute(null);

        vm.Inputs.Single(i => i.PinName == "Cin").IsOn = false;

        vm.IsPlaying.ShouldBeFalse("a new toggle stops the auto-play");
        vm.SelectedTimelineEvent.ShouldBeNull();
    }

    [Fact]
    public async Task DesignEdit_FullAdder_StopsPlayback()
    {
        var vm = await BuildAndToggleCin();
        vm.TogglePlaybackCommand.Execute(null);

        // Add and remove a probe component, restoring the shared fixture canvas for the
        // other tests; the add alone must stop playback and discard the network.
        var probe = new ComponentViewModel(TestComponentFactory.CreateStraightWaveGuide());
        _fixture.Canvas.Components.Add(probe);
        try
        {
            vm.IsPlaying.ShouldBeFalse("a design edit stops the auto-play");
            vm.HasNetwork.ShouldBeFalse();
            vm.TimelineEvents.ShouldBeEmpty();
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
    /// The output-pin badges currently on the canvas, keyed <c>gate.pin</c> — the
    /// named output chips (issue #1067) ride along with their tap's signal name, so
    /// they match by raw pin ref, not by anonymity. Named input chips (issue #1051)
    /// carry the live input bits, not gate output states — playback derivations
    /// exclude them.
    /// </summary>
    private Dictionary<string, bool> BadgeStates() =>
        _fixture.Canvas.LogicGateStates.Badges
            .Where(IsOutputChip)
            .ToDictionary(b => $"{b.GroupName}.{b.PinName}", b => b.IsOne);

    /// <summary>True for a chip on a gate's output tap — anonymous or named (issue #1067).</summary>
    private bool IsOutputChip(LogicGateBadgeViewModel badge) =>
        _fixture.Network.OutputTaps.Values.Any(
            p => p.GateId == badge.GroupName && p.PinName == badge.PinName);

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

    /// <summary>Re-keys a tap-keyed evaluation result by raw <c>gate.pin</c> (see issue #1046).</summary>
    private Dictionary<string, bool> ByGatePin(IReadOnlyDictionary<string, bool> tapKeyed) =>
        _fixture.Network.OutputTaps.ToDictionary(
            tap => $"{tap.Value.GateId}.{tap.Value.PinName}", tap => tapKeyed[tap.Key]);

    /// <summary>
    /// The independently derived state at time t: evaluate every gate output for the
    /// before-toggle inputs, then apply the new value of every switch event whose time
    /// is ≤ t — the mirror of the replay rule, re-derived without the ViewModel.
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
