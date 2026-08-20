using System.Globalization;
using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis.BusView;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Analysis.LogicAnalysis;
using Shouldly;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Kill-review E2E over the merged sequential batch (issue #1120): one user journey
/// across the slices that shipped individually — register core (#1093), toggle UI
/// (#1104), Step button (#1105), 2-bit counter (#1114) and step-timeline entries
/// (#1110). Each slice carries its own tests, but no test walked the journey across
/// all of them at the panel layer — and seams between individually-green slices are
/// where honesty breaks (the black-hole-import lesson of #1001/#1005). The journey,
/// headless at the panel layer (no rendering): (1) load the shipped
/// <c>examples/Logic Gate Counter 2-bit.lun</c> through the real load path
/// (<c>FileOperationsViewModel.LoadDesignFromPathAsync</c>, via the fixture), (2)
/// assemble the network through <see cref="LogicPanelViewModel"/> exactly as the
/// panel's build button does, (3) read the register readout and rest the one named
/// toggle <c>S̄</c> high, (4) press Step five times — the bus row <c>C</c> counts
/// 1, 2, 3, 0, 1 (wrap included) while every step appends a "clock #k" divider block
/// whose commit entries open the block and whose ripple times never decrease, (5)
/// replay backwards across a clock boundary — the badges match the mirror-derived
/// state at every t_k (pattern of <c>LogicPanelStepTimelineTests</c>), and (6) save
/// through the real save path, reload, re-assemble: the register readout, signal
/// names and bus rows are identical and stepping resumes the count from power-up
/// (00). A failing step here is a defect finding, not a test to weaken.
/// </summary>
public class SequentialJourneyE2ETests : IClassFixture<LogicGateCounter2BitExampleTests.CounterFixture>
{
    private const string PresetSignal = "S̄";
    private const string CountBusPrefix = "C";
    private static readonly int[] ExpectedCounts = { 1, 2, 3, 0, 1 };

    private readonly LogicGateCounter2BitExampleTests.CounterFixture _fixture;

    /// <summary>Attaches the shared counter fixture.</summary>
    public SequentialJourneyE2ETests(LogicGateCounter2BitExampleTests.CounterFixture fixture) =>
        _fixture = fixture;

    /// <summary>The resting input bits: the active-low preset toggle stays high while counting.</summary>
    private static Dictionary<string, bool> RestingBits() => new() { [PresetSignal] = true };

    [Fact]
    public async Task CounterJourney_OpenStepWatchReplaySaveLoad_PanelLevel()
    {
        // (1) The example loads through the real load path (fixture init ran
        // FileOperationsViewModel.LoadDesignFromPathAsync) with every gate group.
        _fixture.Groups.Select(g => g.GroupName).ShouldBe(
            new[] { "Q0", "COPY0", "COPY0X", "X0", "R0", "S0", "COPY1", "COPYX", "Q1" },
            ignoreOrder: true,
            customMessage: "step 1: the shipped 2-bit counter loads through the real load path");
        _fixture.Canvas.Connections.Count.ShouldBe(13, "step 1: every counter wire loads");

        // (2) The panel assembles the network through the same command the build button runs.
        var vm = await BuildPanel(_fixture.Canvas);
        vm.HasNetwork.ShouldBeTrue($"step 2: the panel's build assembles the counter — {vm.StatusText}");

        // (3) The register readout lists Q0/Q1 powered up cleared; the single named
        // input toggle S̄ rests on without switching anything (registers hold).
        vm.HasRegisters.ShouldBeTrue("step 3: the counter carries registers, so the panel shows the Step button");
        vm.RegisterStates.Select(r => r.GateName).ShouldBe(new[] { "Q0", "Q1" },
            "step 3: the register readout lists both counter stages");
        vm.RegisterStates.ShouldAllBe(r => r.BitsText == "Y = 0",
            "step 3: both registers power up cleared (C1C0 = 00)");
        vm.Inputs.Select(i => i.PinName).ShouldBe(new[] { PresetSignal },
            "step 3: the counter's one network input reads as the named toggle S̄");
        vm.Inputs[0].IsOn = true;
        vm.TimelineEvents.ShouldBeEmpty("step 3: resting S̄ high switches nothing — the registers hold");

        // (4) Five clock steps: the bus row C counts 1, 2, 3, 0, 1 (wrap included);
        // each step appends a "clock #k" divider block with the register commits at
        // the block start and non-decreasing ripple times across the whole timeline.
        var countBus = BusOf(vm);
        countBus.Members.Select(m => m.PinName).ShouldBe(new[] { "C0", "C1" },
            "step 4: the committed count bits group into one bus row C (C0 = LSB)");
        var previousDivider = "";
        for (var k = 1; k <= ExpectedCounts.Length; k++)
        {
            var rowsBefore = vm.TimelineEvents.Count;
            vm.StepClockCommand.Execute(null);
            var count = ExpectedCounts[k - 1];

            countBus.DecimalValue.ShouldBe(count,
                $"step 4.{k}: after clock #{k} the bus row C shows the decimal count");
            countBus.HeaderText.ShouldBe(
                $"C = {count.ToString(CultureInfo.InvariantCulture)} ({Convert.ToString(count, 2).PadLeft(2, '0')})",
                $"step 4.{k}: the bus header reads the count as a number");
            vm.RegisterStates.Single(r => r.GateName == "Q0").BitsText.ShouldBe($"Y = {count & 1}",
                $"step 4.{k}: Q0 commits the low bit");
            vm.RegisterStates.Single(r => r.GateName == "Q1").BitsText.ShouldBe($"Y = {(count >> 1) & 1}",
                $"step 4.{k}: Q1 commits the high bit");

            var block = vm.TimelineEvents.Skip(rowsBefore).ToList();
            block.ShouldNotBeEmpty($"step 4.{k}: clock #{k} appends its block to the timeline");
            block[0].HasClockBoundary.ShouldBeTrue($"step 4.{k}: a clock divider opens the step's block");
            block[0].ClockBoundaryText.ShouldContain("──",
                customMessage: $"step 4.{k}: the divider renders as a separator line");
            block[0].ClockBoundaryText.ShouldContain($" {k} ",
                customMessage: $"step 4.{k}: the divider counts the clocks");
            block[0].ClockBoundaryText.ShouldNotBe(previousDivider,
                $"step 4.{k}: every clock's divider reads differently");
            block[0].Event.GateId.ShouldBeOneOf("Q0", "Q1",
                $"step 4.{k}: a register commit entry sits at the block start, right behind the divider");
            block.Skip(1).ShouldAllBe(row => !row.HasClockBoundary,
                $"step 4.{k}: only the block's first row carries the divider");
            var times = vm.TimelineEvents.Select(e => e.Event.TimePicoseconds).ToList();
            times.ShouldBe(times.OrderBy(t => t).ToList(),
                $"step 4.{k}: ripple times never decrease across clock blocks");
            previousDivider = block[0].ClockBoundaryText;
        }

        // (5) Replay backwards across the clock boundaries: at every t_k the badges
        // show the mirror-derived state — before-state plus every event with time ≤
        // t_k, derived from the fixture's fresh (never clocked) network, not the panel.
        var beforeState = ByGatePin(_fixture.Network, _fixture.Network.Evaluate(RestingBits()));
        vm.SelectTimelineEventCommand.Execute(vm.TimelineEvents[^1]);
        vm.IsReplayActive.ShouldBeTrue("step 5: selecting a timeline row enters replay");
        var crossedClockBoundary = false;
        for (var k = vm.TimelineEvents.Count - 1; k >= 0; k--)
        {
            BadgesShouldShow(
                StateAt(beforeState, vm.TimelineEvents, vm.TimelineEvents[k].Event.TimePicoseconds),
                $"step 5: replaying event {k} freezes the badges at its t_k");
            if (k > 0)
            {
                crossedClockBoundary |= vm.TimelineEvents[k].HasClockBoundary;
                vm.PreviousTimelineEventCommand.Execute(null);
                vm.SelectedTimelineEvent.ShouldBe(vm.TimelineEvents[k - 1],
                    "step 5: the previous button walks one event earlier, across clock dividers");
            }
        }
        crossedClockBoundary.ShouldBeTrue("step 5: the backward walk crossed at least one clock boundary");
        vm.ExitReplayCommand.Execute(null);
        vm.IsReplayActive.ShouldBeFalse("step 5: leaving replay returns to the live state");
        BadgesShouldShow(StateAt(beforeState, vm.TimelineEvents, double.MaxValue),
            "step 5: back to live shows the settled post-step state: before-state plus every event");

        // (6) Save → load → re-assemble: the register readout, signal names and bus
        // rows are identical, and stepping resumes the count from power-up (00).
        var savedPath = await _fixture.SaveToTempFile();
        try
        {
            var reloaded = await BuildPanel(
                await LogicGateHalfAdderExampleTests.LoadCanvas(savedPath));
            reloaded.RegisterStates.Select(r => r.GateName)
                .ShouldBe(vm.RegisterStates.Select(r => r.GateName).ToArray(),
                    "step 6: the register readout is identical after the save → load round trip");
            reloaded.RegisterStates.ShouldAllBe(r => r.BitsText == "Y = 0",
                "step 6: the reloaded counter powers up cleared again");
            reloaded.Inputs.Select(i => i.PinName).ShouldBe(vm.Inputs.Select(i => i.PinName).ToArray(),
                "step 6: the input signal name survives the round trip");
            reloaded.Outputs.Select(o => o.PinName).ShouldBe(vm.Outputs.Select(o => o.PinName).ToArray(),
                "step 6: every output signal name survives the round trip");
            reloaded.OutputRows.Select(RowSignature).ShouldBe(vm.OutputRows.Select(RowSignature).ToArray(),
                "step 6: the bus rows are identical after the round trip");

            var reloadedBus = BusOf(reloaded);
            reloadedBus.DecimalValue.ShouldBe(0, "step 6: the count restarts at power-up (00)");
            reloaded.Inputs[0].IsOn = true;
            reloaded.StepClockCommand.Execute(null);
            reloadedBus.DecimalValue.ShouldBe(1, "step 6: stepping resumes the count — 00 → 01");
            reloaded.StepClockCommand.Execute(null);
            reloadedBus.DecimalValue.ShouldBe(2, "step 6: …and counts on — 01 → 10");
        }
        finally
        {
            if (File.Exists(savedPath)) File.Delete(savedPath);
        }
    }

    /// <summary>Builds the panel VM over <paramref name="canvas"/> exactly as the UI's build button does.</summary>
    private static async Task<LogicPanelViewModel> BuildPanel(DesignCanvasViewModel canvas)
    {
        var vm = new LogicPanelViewModel();
        vm.Configure(canvas);
        await vm.BuildNetworkCommand.ExecuteAsync(null);
        return vm;
    }

    /// <summary>The output bus row of the count bits (<c>C0</c>/<c>C1</c>).</summary>
    private static LogicSignalBusOutputViewModel BusOf(LogicPanelViewModel vm) =>
        vm.OutputRows.OfType<LogicSignalBusOutputViewModel>().Single(b => b.Prefix == CountBusPrefix);

    /// <summary>A bus row's structural signature: prefix and member taps, or the plain row's tap.</summary>
    private static string RowSignature(LogicOutputRowViewModel row) => row switch
    {
        LogicSignalBusOutputViewModel bus =>
            $"bus:{bus.Prefix}({string.Join(",", bus.Members.Select(m => m.PinName))})",
        LogicNetworkOutputViewModel single => $"row:{single.PinName}",
        _ => throw new InvalidOperationException($"Unknown output row type {row.GetType().Name}."),
    };

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

    /// <summary>The output-pin badges currently on the canvas, keyed <c>gate.pin</c> (named input chips excluded).</summary>
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
}
