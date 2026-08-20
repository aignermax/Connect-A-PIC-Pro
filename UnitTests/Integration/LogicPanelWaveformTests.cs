using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Core;
using Shouldly;
using UnitTests.Analysis.LogicAnalysis;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// ViewModel tests for the Logic panel's waveform strip (issue #1129, rung 5
/// visualizer): the shipped <c>examples/Logic Gate Counter 2-bit.lun</c> stepped
/// twice yields a lane per named signal with C0 toggling 0→1→0 and C1 rising on
/// the wrap — the square waves a student reads off the strip — with monotone x
/// per lane and a divider per clock. The replay cursor follows the selected
/// timeline row. A network without named signals shows no strip at all.
/// </summary>
public class LogicPanelWaveformTests : IClassFixture<LogicGateCounter2BitExampleTests.CounterFixture>
{
    private const string PresetSignal = "S̄";
    private const string LowBitTap = "C0";
    private const string HighBitTap = "C1";
    private const double OrThreshold = 0.25;

    private readonly LogicGateCounter2BitExampleTests.CounterFixture _fixture;

    /// <summary>Attaches the shared counter fixture.</summary>
    public LogicPanelWaveformTests(LogicGateCounter2BitExampleTests.CounterFixture fixture) =>
        _fixture = fixture;

    [Fact]
    public async Task TwoSteps_Counter_LanesShowTheTogglingBitsWithMonotoneX()
    {
        var vm = await BuildCounter();
        vm.Inputs.Single(i => i.PinName == PresetSignal).IsOn = true;
        vm.TimelineEvents.ShouldBeEmpty("the preset feeds only a register input — resting it high settles nothing");

        vm.StepClockCommand.Execute(null);
        vm.StepClockCommand.Execute(null);

        var model = vm.Waveform.ShouldNotBeNull("a stepped timeline has a waveform");
        model.Lanes[0].SignalName.ShouldBe(PresetSignal, "named inputs lead the lane order");
        model.Lanes.Skip(1).Select(l => l.SignalName).ShouldBe(
            new[] { LowBitTap, HighBitTap, "X", "R", "S" }, ignoreOrder: true,
            customMessage: "the named taps follow — the register outputs read under their tap names");

        var preset = model.Lanes[0];
        preset.Edges.ShouldBeEmpty("the input lane holds its toggled level through the whole window");
        preset.LevelAt(0).ShouldBeTrue();
        preset.LevelAt(1).ShouldBeTrue();

        var c0 = model.Lanes.Single(l => l.SignalName == LowBitTap);
        c0.InitialLevel.ShouldBeFalse("the counter powers up cleared");
        c0.Edges.Select(e => e.NewLevel).ShouldBe(new[] { true, false },
            "C0 toggles 0→1 on the first clock and wraps 1→0 on the second");
        c0.Edges[0].XFraction.ShouldBe(0.0, "the first commit opens the timeline at t = 0");
        c0.Edges[1].XFraction.ShouldBeGreaterThan(0.0);
        c0.LevelAt(c0.Edges[1].XFraction / 2).ShouldBeTrue("C0 is high between the two clocks");
        c0.LevelAt(1).ShouldBeFalse("C0 rests low after the wrap");
        c0.LiveLevel.ShouldBeFalse();

        var c1 = model.Lanes.Single(l => l.SignalName == HighBitTap);
        c1.InitialLevel.ShouldBeFalse();
        c1.Edges.Select(e => e.NewLevel).ShouldBe(new[] { true },
            "C1 rises exactly once — when C0 wraps");
        c1.LevelAt(1).ShouldBeTrue();
        c1.LiveLevel.ShouldBeTrue("after two clocks the count reads C1C0 = 10");

        foreach (var lane in model.Lanes)
        {
            var fractions = lane.Edges.Select(e => e.XFraction).ToList();
            fractions.ShouldBe(fractions.OrderBy(f => f).ToList(),
                $"lane '{lane.SignalName}' must keep monotone x");
        }

        model.Dividers.Count.ShouldBe(2, "two stepped clocks drew two boundaries");
        model.Dividers[0].XFraction.ShouldBe(0.0);
        model.Dividers[1].XFraction.ShouldBeGreaterThan(0.0);
        model.Dividers[1].Label.ShouldNotBe(model.Dividers[0].Label, "the dividers count the clocks");
    }

    [Fact]
    public async Task ReplayCursor_FollowsTheSelectedRow_AndClearsOnExit()
    {
        var vm = await BuildCounter();
        vm.Inputs.Single(i => i.PinName == PresetSignal).IsOn = true;
        vm.StepClockCommand.Execute(null);

        vm.Waveform!.CursorXFraction.ShouldBeNull("no replay — no cursor");

        var row = vm.TimelineEvents[^1];
        vm.SelectTimelineEventCommand.Execute(row);

        var model = vm.Waveform!;
        var cursor = model.CursorXFraction.ShouldNotBeNull("the replayed instant marks the strip");
        var expected = row.Event.TimePicoseconds / model.EndTimePicoseconds;
        cursor.ShouldBe(expected, 1e-9);

        vm.ExitReplayCommand.Execute(null);
        vm.Waveform!.CursorXFraction.ShouldBeNull("back to live clears the cursor");
    }

    [Fact]
    public async Task CombinationalNetworkWithoutNamedSignals_ShowsNoWaveform()
    {
        var canvas = new DesignCanvasViewModel();
        canvas.Components.Add(new ComponentViewModel(CombinationalOrGate("OR1")));
        var vm = new LogicPanelViewModel();
        vm.Configure(canvas);
        await vm.BuildNetworkCommand.ExecuteAsync(null);
        vm.HasNetwork.ShouldBeTrue(vm.StatusText);

        vm.Inputs.Single(i => i.PinName == "OR1.a").IsOn = true;

        vm.TimelineEvents.ShouldNotBeEmpty("the toggle ripples through the gate");
        vm.HasWaveform.ShouldBeFalse(
            "unnamed pins stay out of slice 1 — a network without named signals has no lanes");
    }

    /// <summary>Builds the counter's network on the fixture canvas.</summary>
    private async Task<LogicPanelViewModel> BuildCounter()
    {
        var vm = new LogicPanelViewModel();
        vm.Configure(_fixture.Canvas);
        await vm.BuildNetworkCommand.ExecuteAsync(null);
        vm.HasNetwork.ShouldBeTrue(vm.StatusText);
        vm.HasRegisters.ShouldBeTrue("the counter's two stages are designated registers");
        return vm;
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
