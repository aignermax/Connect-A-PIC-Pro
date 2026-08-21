using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis.BusView;
using Shouldly;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Kill-review E2E over Run mode (issue #1135): the full user journey over the
/// shipped <c>examples/Logic Gate PC 2-bit.lun</c> (#1128) — the demo loop a
/// student plays with: load the PC, press Run, watch it count, flip LOAD
/// mid-run, see the external word land on the next tick, stop, reset. Run mode
/// (#1111) is otherwise tested against a synthetic two-register ring; nothing
/// proved the journey over a real shipped example, and seams between
/// individually-green slices are where honesty breaks (the pattern of
/// <c>SequentialJourneyE2ETests</c>, issue #1120). Headless at the panel layer
/// (no rendering): the real load path
/// (<c>FileOperationsViewModel.LoadDesignFromPathAsync</c>, via the fixture),
/// the panel's own build command, and an injected fake
/// <see cref="ILogicRunClock"/> fired synchronously — production wiring
/// untouched. A failing step here is a defect finding, not a test to weaken.
/// </summary>
public class ProgramCounterRunJourneyE2ETests
    : IClassFixture<LogicGatePc2BitExampleTests.PcFixture>
{
    private const string LoadSignal = "LOAD";
    private const string LowLoadSignal = "L0";
    private const string HighLoadSignal = "L1";
    private const string LowBitTap = "C0";
    private const string HighBitTap = "C1";
    private const string LowRegister = "REG0";
    private const string HighRegister = "REG1";

    private readonly LogicGatePc2BitExampleTests.PcFixture _fixture;

    /// <summary>Attaches the shared PC fixture.</summary>
    public ProgramCounterRunJourneyE2ETests(LogicGatePc2BitExampleTests.PcFixture fixture) =>
        _fixture = fixture;

    [Fact]
    public async Task PcRunJourney_CountLoadMidRunStopReset_PanelLevel()
    {
        // (1) The example loads through the real load path (fixture init ran
        // FileOperationsViewModel.LoadDesignFromPathAsync); the panel's build
        // command assembles the network, the register readout lists REG0/REG1,
        // and the L/C bus rows show the load word and the count as decimals.
        var clock = new FakeLogicRunClock();
        var vm = new LogicPanelViewModel(clock);
        vm.Configure(_fixture.Canvas);
        await vm.BuildNetworkCommand.ExecuteAsync(null);
        vm.HasNetwork.ShouldBeTrue($"step 1: the panel's build assembles the PC — {vm.StatusText}");
        vm.HasRegisters.ShouldBeTrue("step 1: the PC carries registers, so the panel shows Run");
        vm.RegisterStates.Select(r => r.GateName).ShouldBe(new[] { LowRegister, HighRegister },
            "step 1: the register readout lists both PC stages");
        var countBus = CountBus(vm);
        countBus.Members.Select(m => m.PinName).ShouldBe(new[] { LowBitTap, HighBitTap },
            "step 1: the committed count bits group into one bus row C (C0 = LSB)");
        countBus.DecimalValue.ShouldBe(0, "step 1: the PC powers up cleared (C1C0 = 00)");
        var loadBus = vm.InputRows.OfType<LogicSignalBusInputViewModel>().Single();
        loadBus.Prefix.ShouldBe("L", "step 1: the external load bits group into one bus row L");
        loadBus.Members.Select(m => m.PinName).ShouldBe(new[] { LowLoadSignal, HighLoadSignal });
        vm.Inputs.Select(i => i.PinName).ShouldBe(
            new[] { LoadSignal, LowLoadSignal, HighLoadSignal }, ignoreOrder: true,
            customMessage: "step 1: the three network toggles read LOAD, L0, L1");

        // (2) Run with the injected fake clock: three ticks with LOAD = 0 count
        // C = 1, 2, 3, and every tick appends exactly one "── clock #k ──"
        // divider block to the timeline.
        vm.ToggleRunCommand.Execute(null);
        vm.IsRunning.ShouldBeTrue("step 2: Run starts the auto-clock");
        clock.IsStarted.ShouldBeTrue("step 2: the injected clock received Start");
        for (var k = 1; k <= 3; k++)
        {
            var dividersBefore = vm.TimelineEvents.Count(e => e.HasClockBoundary);
            clock.FireTick();
            countBus.DecimalValue.ShouldBe(k,
                $"step 2.{k}: tick #{k} with LOAD = 0 counts the bus row C up by one");
            var dividers = vm.TimelineEvents.Where(e => e.HasClockBoundary).ToList();
            dividers.Count.ShouldBe(dividersBefore + 1,
                $"step 2.{k}: tick #{k} appended exactly one clock divider block");
            dividers[^1].ClockBoundaryText.ShouldContain("──",
                customMessage: $"step 2.{k}: the divider renders as a separator line");
            dividers[^1].ClockBoundaryText.ShouldContain($" {k} ",
                customMessage: $"step 2.{k}: the divider counts the clocks");
        }

        // (3) Mid-run: LOAD = 1 with L = 2 (L1 = 1, L0 = 0) — the next tick
        // commits the external word instead of incrementing.
        SetInputs(vm, load: true, l0: false, l1: true);
        loadBus.DecimalValue.ShouldBe(2, "step 3: the L bus row reads the pending word 2");
        vm.IsRunning.ShouldBeTrue("step 3: flipping the toggles mid-run does not stop the run");
        clock.FireTick();
        countBus.DecimalValue.ShouldBe(2,
            "step 3: the tick commits the external word L = 2, not the incremented count");
        vm.TimelineEvents.Where(e => e.HasClockBoundary).ShouldHaveSingleItem()
            .ClockBoundaryText.ShouldContain(" 1 ",
                customMessage: "step 3: the toggle restarted the timeline at the new input " +
                    "assignment (issue #1045), so the load tick opens clock #1 of the new window");

        // (4) LOAD back to 0: the next tick counts on from the loaded value.
        SetInputs(vm, load: false, l0: false, l1: false);
        clock.FireTick();
        countBus.DecimalValue.ShouldBe(3,
            "step 4: with LOAD = 0 counting resumes from the loaded value — 2 → 3");

        // (5) Stop: stray ticks queued after Stop change nothing; the waveform
        // lanes and the timeline agree on the last committed state, and the
        // register readout matches the canvas badges.
        vm.ToggleRunCommand.Execute(null);
        vm.IsRunning.ShouldBeFalse("step 5: toggling again stops the auto-clock");
        clock.IsStarted.ShouldBeFalse("step 5: the injected clock received Stop");
        var eventsAfterStop = vm.TimelineEvents.Count;
        clock.FireTick();
        clock.FireTick();
        vm.TimelineEvents.Count.ShouldBe(eventsAfterStop,
            "step 5: stray ticks after Stop append nothing to the timeline");
        countBus.DecimalValue.ShouldBe(3, "step 5: stray ticks after Stop change nothing");

        vm.HasWaveform.ShouldBeTrue("step 5: the waveform strip shows the run's trace");
        vm.Waveform!.Dividers.Count.ShouldBe(
            vm.TimelineEvents.Count(e => e.HasClockBoundary),
            "step 5: the waveform's dividers are exactly the timeline's clock blocks");
        var lastBlock = vm.TimelineEvents
            .Skip(vm.TimelineEvents.IndexOf(vm.TimelineEvents.Last(e => e.HasClockBoundary)))
            .ToList();
        lastBlock[0].Event.GateId.ShouldBe(LowRegister,
            "step 5: the resume tick's block opens with REG0's commit (C0 rose 0 → 1; REG1 held)");
        lastBlock[0].Event.NewValue.ShouldBeTrue(
            "step 5: the timeline's last register commit agrees with the committed state");
        var lanes = vm.Waveform.Lanes.ToDictionary(lane => lane.SignalName);
        foreach (var (tap, register, expected) in new[]
                 {
                     (LowBitTap, LowRegister, true), (HighBitTap, HighRegister, true),
                 })
        {
            lanes[tap].LiveLevel.ShouldBe(expected,
                $"step 5: the {tap} lane's live level shows the last committed state (C = 3)");
            lanes[tap].LevelAt(1.0).ShouldBe(expected,
                $"step 5: the {tap} lane ends the trace on the last committed state");
            vm.RegisterStates.Single(r => r.GateName == register).BitsText
                .ShouldBe($"Y = {(expected ? 1 : 0)}",
                    $"step 5: the register readout shows {register}'s committed bit");
            _fixture.Canvas.LogicGateStates.Badges
                .Single(b => b.GroupName == register && b.PinName == "Y").IsOne
                .ShouldBe(expected,
                    $"step 5: the canvas badge of {register} matches the register readout");
        }

        // (6) Reset: the count snaps back to power-up, the clock counter
        // restarts, and one more Run tick counts to 1 behind a "clock #1"
        // divider again.
        vm.ResetRegistersCommand.Execute(null);
        countBus.DecimalValue.ShouldBe(0, "step 6: Reset snaps the count back to power-up");
        vm.TimelineEvents.ShouldAllBe(e => !e.HasClockBoundary,
            "step 6: the timeline restarts at the reset's fresh settle — no clock blocks");
        vm.ToggleRunCommand.Execute(null);
        clock.FireTick();
        countBus.DecimalValue.ShouldBe(1, "step 6: one more Run tick counts to 1 again");
        var dividersAfterReset = vm.TimelineEvents.Where(e => e.HasClockBoundary).ToList();
        dividersAfterReset.Count.ShouldBe(1,
            "step 6: exactly one clock block follows the fresh settle");
        dividersAfterReset[0].ClockBoundaryText.ShouldContain(" 1 ",
            customMessage: "step 6: the clock counter restarted — the tick is clock #1 again");
        vm.ToggleRunCommand.Execute(null);
    }

    /// <summary>The output bus row of the count bits (<c>C0</c>/<c>C1</c>).</summary>
    private static LogicSignalBusOutputViewModel CountBus(LogicPanelViewModel vm) =>
        vm.OutputRows.OfType<LogicSignalBusOutputViewModel>().Single(b => b.Prefix == "C");

    /// <summary>Sets the three network toggles the way the user flips them in the panel.</summary>
    private static void SetInputs(LogicPanelViewModel vm, bool load, bool l0, bool l1)
    {
        vm.Inputs.Single(i => i.PinName == LoadSignal).IsOn = load;
        vm.Inputs.Single(i => i.PinName == LowLoadSignal).IsOn = l0;
        vm.Inputs.Single(i => i.PinName == HighLoadSignal).IsOn = l1;
    }

    /// <summary>
    /// Manually fired <see cref="ILogicRunClock"/>: only ticks when the test says
    /// so — no wall-clock involved (pattern of <c>LogicPanelRunModeTests</c>).
    /// </summary>
    private sealed class FakeLogicRunClock : ILogicRunClock
    {
        public bool IsStarted { get; private set; }

        public event EventHandler? Tick;

        public void Start(TimeSpan interval) => IsStarted = true;

        public void Stop() => IsStarted = false;

        public void FireTick() => Tick?.Invoke(this, EventArgs.Empty);
    }
}
