using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Analysis.LogicAnalysis;
using CAP_Core.Components.Core;
using Shouldly;
using UnitTests.Analysis.LogicAnalysis;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// ViewModel tests for clock steps on the Logic panel's event timeline (issue
/// #1110, rung 5 visualizer): on the shipped <c>examples/Logic Gate SR-Latch.lun</c>,
/// pulling S̄ low and stepping appends a "clock #k" divider plus one commit entry
/// per changed register output; the next step's entries follow with non-decreasing
/// times. Replay and auto-play walk across the clock boundary with the invariant
/// of issue #1058 — the badges at t_k equal the before-state plus every event
/// with time ≤ t_k, mirror-derived from a fresh network without the panel. A
/// quiet clock appends nothing, and a purely combinational network never shows
/// clock entries.
/// </summary>
public class LogicPanelStepTimelineTests : IClassFixture<LogicGateSrLatchExampleTests.SrLatchFixture>
{
    private const string SetSignal = "S̄";
    private const string ResetSignal = "R̄";
    private const double OrThreshold = 0.25;

    private readonly LogicGateSrLatchExampleTests.SrLatchFixture _fixture;

    /// <summary>Attaches the shared SR-latch fixture.</summary>
    public LogicPanelStepTimelineTests(LogicGateSrLatchExampleTests.SrLatchFixture fixture) =>
        _fixture = fixture;

    [Fact]
    public async Task StepClock_SrLatch_CommitEntriesAppearBehindClockDividers()
    {
        var vm = await BuildLatchAtRest();
        vm.TimelineEvents.ShouldBeEmpty("an all-register network produces no settle events");

        vm.Inputs.Single(i => i.PinName == SetSignal).IsOn = false;
        vm.TimelineEvents.ShouldBeEmpty("registers hold — pulling S̄ low switches nothing yet");

        vm.StepClockCommand.Execute(null);

        vm.TimelineEvents.Count.ShouldBe(2, "both cross-coupled NAND registers commit on the first edge");
        var qCommit = vm.TimelineEvents[0];
        qCommit.HasClockBoundary.ShouldBeTrue("a clock divider opens the step's block");
        qCommit.ClockBoundaryText.ShouldNotBeNullOrEmpty();
        qCommit.Event.GateId.ShouldBe("NANDQ");
        qCommit.Event.OutputPin.ShouldBe("Y");
        qCommit.Event.NewValue.ShouldBeTrue("Q commits to 1 — the latch is set");
        vm.TimelineEvents[1].HasClockBoundary.ShouldBeFalse("only the block's first row carries the divider");
        vm.TimelineEvents[1].Event.GateId.ShouldBe("NANDQB");
        vm.TimelineEvents[1].Event.NewValue.ShouldBeTrue("Q̄ commits to 1 as well — the first edge's transient");

        vm.StepClockCommand.Execute(null);

        vm.TimelineEvents.Count.ShouldBe(3, "the second clock settles the latch: only Q̄ falls");
        var settle = vm.TimelineEvents[2];
        settle.HasClockBoundary.ShouldBeTrue("the second step opens its own divider");
        settle.ClockBoundaryText.ShouldNotBe(qCommit.ClockBoundaryText, "the divider counts the clocks");
        settle.Event.GateId.ShouldBe("NANDQB");
        settle.Event.NewValue.ShouldBeFalse();
        var times = vm.TimelineEvents.Select(e => e.Event.TimePicoseconds).ToList();
        times.ShouldBe(times.OrderBy(t => t).ToList(),
            "clock blocks append after the preceding entries with non-decreasing times");
        vm.RegisterStates.Single(r => r.GateName == "NANDQ").BitsText.ShouldBe("Y = 1");
        vm.RegisterStates.Single(r => r.GateName == "NANDQB").BitsText.ShouldBe("Y = 0");
    }

    [Fact]
    public async Task StepClock_QuietClock_AppendsNothing()
    {
        var vm = await BuildLatchAtRest();
        vm.Inputs.Single(i => i.PinName == SetSignal).IsOn = false;
        vm.StepClockCommand.Execute(null);
        vm.StepClockCommand.Execute(null);
        var entries = vm.TimelineEvents.ToList();

        vm.StepClockCommand.Execute(null);

        vm.TimelineEvents.SequenceEqual(entries).ShouldBeTrue(
            "a quiet clock has nothing to record — not even a divider");
    }

    [Fact]
    public async Task Replay_AcrossTwoClockSteps_BadgesMatchDerivedStateAtEachInstant()
    {
        var vm = await BuildLatchAtRest();
        vm.Inputs.Single(i => i.PinName == SetSignal).IsOn = false;
        vm.StepClockCommand.Execute(null);
        vm.StepClockCommand.Execute(null);
        var beforeState = await MirrorBeforeState();

        for (var k = 0; k < vm.TimelineEvents.Count; k++)
        {
            vm.SelectTimelineEventCommand.Execute(vm.TimelineEvents[k]);
            BadgesShouldShow(
                StateAt(beforeState, vm.TimelineEvents, vm.TimelineEvents[k].Event.TimePicoseconds),
                $"event {k} across the clock boundary freezes the badges at its t_k");
        }

        vm.ExitReplayCommand.Execute(null);
        BadgesShouldShow(
            StateAt(beforeState, vm.TimelineEvents, double.MaxValue),
            "back to live shows the settled post-step state: before-state plus every event");
    }

    [Fact]
    public async Task Playback_TicksAcrossTheClockBoundary()
    {
        var vm = await BuildLatchAtRest();
        vm.Inputs.Single(i => i.PinName == SetSignal).IsOn = false;
        vm.StepClockCommand.Execute(null);
        vm.StepClockCommand.Execute(null);

        vm.TogglePlaybackCommand.Execute(null);
        vm.SelectedTimelineEvent.ShouldBe(vm.TimelineEvents[0], "play starts at the first commit");

        vm.AdvancePlaybackTick();
        vm.AdvancePlaybackTick();
        vm.SelectedTimelineEvent.ShouldBe(vm.TimelineEvents[2],
            "the tick walks straight across the clock divider — display metadata, not a barrier");
        vm.TimelineEvents[2].HasClockBoundary.ShouldBeTrue();

        vm.AdvancePlaybackTick();
        vm.IsPlaying.ShouldBeFalse("the tick after the last event ends playback in the live state");
    }

    [Fact]
    public async Task Timeline_PurelyCombinationalNetwork_NeverShowsClockEntries()
    {
        var canvas = new DesignCanvasViewModel();
        canvas.Components.Add(new ComponentViewModel(CombinationalOrGate("OR1")));
        var vm = new LogicPanelViewModel();
        vm.Configure(canvas);
        await vm.BuildNetworkCommand.ExecuteAsync(null);
        vm.HasNetwork.ShouldBeTrue(vm.StatusText);

        vm.Inputs.Single(i => i.PinName == "OR1.a").IsOn = true;

        vm.TimelineEvents.ShouldNotBeEmpty("the toggle ripples through the gate");
        vm.TimelineEvents.ShouldAllBe(
            row => !row.HasClockBoundary,
            "no clock divider can appear — the network has no registers to clock");
        vm.StepClockCommand.CanExecute(null).ShouldBeFalse();
    }

    /// <summary>Builds the latch's network on the fixture canvas and rests both active-low inputs high.</summary>
    private async Task<LogicPanelViewModel> BuildLatchAtRest()
    {
        var vm = new LogicPanelViewModel();
        vm.Configure(_fixture.Canvas);
        await vm.BuildNetworkCommand.ExecuteAsync(null);
        vm.HasNetwork.ShouldBeTrue(vm.StatusText);
        vm.HasRegisters.ShouldBeTrue("both NAND gates of the latch are designated registers");
        vm.Inputs.Single(i => i.PinName == SetSignal).IsOn = true;
        vm.Inputs.Single(i => i.PinName == ResetSignal).IsOn = true;
        return vm;
    }

    /// <summary>
    /// The settled state the displayed timeline starts from, derived from a fresh
    /// mirror network (registers power up cleared, S̄ pulled low, R̄ resting high)
    /// — never from the panel under test.
    /// </summary>
    private async Task<Dictionary<string, bool>> MirrorBeforeState()
    {
        var mirror = await LogicGateMuxExampleTests.AssembleNetwork(_fixture.Canvas);
        return ByGatePin(mirror, mirror.Evaluate(_fixture.InputBits(set: false, reset: true)));
    }

    /// <summary>Re-keys a tap-keyed evaluation result by raw <c>gate.pin</c> (see issue #1046).</summary>
    private static Dictionary<string, bool> ByGatePin(
        LogicNetworkEvaluator network, IReadOnlyDictionary<string, bool> tapKeyed) =>
        network.OutputTaps.ToDictionary(
            tap => $"{tap.Value.GateId}.{tap.Value.PinName}", tap => tapKeyed[tap.Key]);

    /// <summary>
    /// The independently derived state at time t: the before-state plus the new
    /// value of every displayed event whose time is ≤ t — the rule of issue #1058
    /// re-derived in test code without touching the ViewModel's replay path.
    /// </summary>
    private static Dictionary<string, bool> StateAt(
        IReadOnlyDictionary<string, bool> beforeState,
        IEnumerable<LogicTimelineEventViewModel> displayedEvents,
        double timePicoseconds)
    {
        var state = new Dictionary<string, bool>(beforeState);
        foreach (var row in displayedEvents)
        {
            if (row.Event.TimePicoseconds > timePicoseconds)
                break;
            state[$"{row.Event.GateId}.{row.Event.OutputPin}"] = row.Event.NewValue;
        }
        return state;
    }

    /// <summary>
    /// The output-pin badges currently on the canvas, keyed <c>gate.pin</c>. Named
    /// input chips (issue #1051) carry the live input bits, not gate output
    /// states — replay derivations exclude them.
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

    /// <summary>A combinational combiner group with the OR-reading assignment persisted.</summary>
    private static ComponentGroup CombinationalOrGate(string groupName)
    {
        var group = LogicGateFixtureFactory.CreateCombinerGroup();
        group.GroupName = groupName;
        group.TruthTablePinAssignment = new TruthTablePinAssignment
        {
            InputPinNames = new List<string> { "a", "b" },
            OutputPinNames = new List<string> { "y" },
            BiasPinNames = new List<string>(),
            Threshold = OrThreshold,
            IsRegister = false,
        };
        group.EnsureSMatrixComputed();
        return group;
    }
}
