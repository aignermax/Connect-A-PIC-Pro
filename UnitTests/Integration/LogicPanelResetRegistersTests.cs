using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis.BusView;
using Shouldly;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// ViewModel tests for the Logic panel's Reset button (issue #1127, rung 5
/// visualizer) on the shipped <c>examples/Logic Gate Counter 2-bit.lun</c>: after
/// three clock steps the count reads 3, one Reset returns every register to its
/// power-up state without rebuilding the network — the bus row C reads 0, the
/// register readout and the canvas badges show the cleared state, and the event
/// timeline restarts at the reset's fresh settle phase, so the next Step counts 1
/// behind a "clock #1" divider again. Reset also exits replay and stops playback,
/// and a purely combinational network keeps the button disabled.
/// </summary>
public class LogicPanelResetRegistersTests
    : IClassFixture<LogicGateCounter2BitExampleTests.CounterFixture>
{
    private const string PresetSignal = "S̄";

    private readonly LogicGateCounter2BitExampleTests.CounterFixture _fixture;

    /// <summary>Attaches the shared counter fixture.</summary>
    public LogicPanelResetRegistersTests(LogicGateCounter2BitExampleTests.CounterFixture fixture) =>
        _fixture = fixture;

    [Fact]
    public async Task ResetRegisters_AfterThreeSteps_CountReturnsToPowerUp()
    {
        var vm = await BuildCounter();
        Step(vm, 3);
        CountBus(vm).DecimalValue.ShouldBe(3, "three clocks counted C1C0 = 11");
        vm.TimelineEvents.Count(e => e.HasClockBoundary).ShouldBe(3,
            "each step opened its own clock divider");

        vm.ResetRegistersCommand.Execute(null);

        CountBus(vm).DecimalValue.ShouldBe(0, "the count snapped back to power-up");
        CountBus(vm).HeaderText.ShouldBe("C = 0 (00)");
        vm.RegisterStates.Select(r => r.BitsText).ShouldBe(new[] { "Y = 0", "Y = 0" },
            "both register readout rows show the cleared state");
        _fixture.Canvas.LogicGateStates.Badges
            .Single(b => b.GroupName == "Q0" && b.PinName == "Y").IsOne
            .ShouldBeFalse("the canvas badges re-settled with the cleared state");
        _fixture.Canvas.LogicGateStates.Badges
            .Single(b => b.GroupName == "Q1" && b.PinName == "Y").IsOne
            .ShouldBeFalse();

        vm.HasTimelineEvents.ShouldBeTrue("the reset's fresh settle replaces the clock history");
        vm.TimelineEvents.ShouldAllBe(e => !e.HasClockBoundary,
            "a settle phase carries no clock divider");
        vm.TimelineEvents.Count.ShouldBeGreaterThan(2,
            "the two commits ripple through the counter's copy gates");
        vm.TimelineEvents[0].Event.GateId.ShouldBe("Q0");
        vm.TimelineEvents[1].Event.GateId.ShouldBe("Q1");
        vm.TimelineEvents[0].Event.TimePicoseconds.ShouldBe(0.0);
        vm.TimelineEvents[1].Event.TimePicoseconds.ShouldBe(0.0);
        vm.TimelineEvents[0].Event.NewValue.ShouldBeFalse("both commits fall back to 0");
        vm.TimelineEvents[1].Event.NewValue.ShouldBeFalse();
    }

    [Fact]
    public async Task ResetRegisters_ThenStep_CountsOneBehindClockOneDivider()
    {
        var vm = await BuildCounter();
        Step(vm, 3);
        vm.ResetRegistersCommand.Execute(null);

        vm.StepClockCommand.Execute(null);

        CountBus(vm).DecimalValue.ShouldBe(1, "the next clock counts from power-up again");
        var divider = vm.TimelineEvents.Where(e => e.HasClockBoundary).ToList();
        divider.Count.ShouldBe(1, "exactly one clock block follows the fresh settle");
        divider[0].ClockBoundaryText.ShouldBe(
            string.Format(LocalizationService.Instance.Translate("LogicPanel.ClockDivider"), 1),
            "the clock counter restarted — the next step is clock #1 again");
    }

    [Fact]
    public async Task ResetRegisters_ExitsReplayAndStopsPlayback()
    {
        var vm = await BuildCounter();
        Step(vm, 2);
        vm.TogglePlaybackCommand.Execute(null);
        vm.IsPlaying.ShouldBeTrue("the ripple auto-plays from the first commit");

        vm.ResetRegistersCommand.Execute(null);

        vm.IsPlaying.ShouldBeFalse("reset stops the auto-play");
        vm.SelectedTimelineEvent.ShouldBeNull("reset exits replay");
        vm.IsReplayActive.ShouldBeFalse();
        vm.NextTimelineEventCommand.CanExecute(null).ShouldBeTrue(
            "the fresh settle is steppable like any timeline");
    }

    [Fact]
    public async Task ResetRegisters_QuietReset_KeepsTimelineEmpty()
    {
        var vm = await BuildCounter();

        vm.ResetRegistersCommand.Execute(null);

        CountBus(vm).DecimalValue.ShouldBe(0);
        vm.HasTimelineEvents.ShouldBeFalse(
            "power-up was never left — nothing committed, nothing rippled");
        vm.StepClockCommand.Execute(null);
        CountBus(vm).DecimalValue.ShouldBe(1, "the counter still counts from a quiet reset");
    }

    /// <summary>Builds the counter's network on the fixture canvas and rests the preset toggle high.</summary>
    private async Task<LogicPanelViewModel> BuildCounter()
    {
        var vm = new LogicPanelViewModel();
        vm.Configure(_fixture.Canvas);
        await vm.BuildNetworkCommand.ExecuteAsync(null);
        vm.HasNetwork.ShouldBeTrue(vm.StatusText);
        vm.HasRegisters.ShouldBeTrue("Q0 and Q1 are designated registers");
        vm.ResetRegistersCommand.CanExecute(null).ShouldBeTrue();
        vm.Inputs.Single(i => i.PinName == PresetSignal).IsOn = true;
        return vm;
    }

    /// <summary>Presses the Step button <paramref name="count"/> times.</summary>
    private static void Step(LogicPanelViewModel vm, int count)
    {
        for (var k = 0; k < count; k++)
            vm.StepClockCommand.Execute(null);
    }

    /// <summary>The bus row of the counter's C0/C1 output family.</summary>
    private static LogicSignalBusOutputViewModel CountBus(LogicPanelViewModel vm) =>
        vm.OutputRows.OfType<LogicSignalBusOutputViewModel>().Single(b => b.Prefix == "C");
}
