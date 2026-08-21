using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CAP.Avalonia.ViewModels.Canvas;
using Shouldly;
using Xunit;
using static UnitTests.Helpers.LogicRingTestFixture;

namespace UnitTests.Integration;

/// <summary>
/// ViewModel tests for the Logic panel's Run (auto-clock) mode (issue #1111, rung 5
/// "watch it execute"): the Run button clocks the registered network on its own at
/// the selected cadence, every tick behaving exactly like one Step press. The tick
/// source is an injected <see cref="ILogicRunClock"/> fake — tests fire ticks
/// synchronously and assert the requested periods, never wall-clock time. The
/// circuit is the same two-register ring as <c>LogicPanelStepClockTests</c> (R1.y →
/// R2.a, R2.y → R1.a): once seeded, every clock flips every register exactly once.
/// </summary>
public class LogicPanelRunModeTests
{
    [Fact]
    public async Task RunMode_ThreeTicks_MatchesThreeStepPresses()
    {
        var stepCanvas = RingCanvas();
        var stepped = new LogicPanelViewModel(new FakeLogicRunClock());
        stepped.Configure(stepCanvas);
        await stepped.BuildNetworkCommand.ExecuteAsync(null);

        var runCanvas = RingCanvas();
        var clock = new FakeLogicRunClock();
        var running = new LogicPanelViewModel(clock);
        running.Configure(runCanvas);
        await running.BuildNetworkCommand.ExecuteAsync(null);

        stepped.Inputs.Single(i => i.PinName == "R1.b").IsOn = true;
        running.Inputs.Single(i => i.PinName == "R1.b").IsOn = true;

        running.ToggleRunCommand.Execute(null);
        running.IsRunning.ShouldBeTrue();

        // First clock commits the seed; then the seed input drops, as in the Step tests.
        stepped.StepClockCommand.Execute(null);
        clock.FireTick();
        stepped.Inputs.Single(i => i.PinName == "R1.b").IsOn = false;
        running.Inputs.Single(i => i.PinName == "R1.b").IsOn = false;

        for (var tick = 0; tick < 2; tick++)
        {
            stepped.StepClockCommand.Execute(null);
            clock.FireTick();
        }

        SnapshotOf(running, runCanvas).ShouldBe(SnapshotOf(stepped, stepCanvas),
            "Run + 3 ticks commits exactly the state that 3 Step presses commit");
        RingState(running).ShouldBe((true, false), "the seeded 1 completed the loop on the third clock");
        running.IsRunning.ShouldBeTrue("the auto-clock keeps running after the ticks");
    }

    [Fact]
    public async Task RunMode_CombinationalNetwork_ToggleRunIsDisabled()
    {
        var canvas = new DesignCanvasViewModel();
        canvas.Components.Add(new ComponentViewModel(OrGate("OR1", isRegister: false)));
        var clock = new FakeLogicRunClock();
        var vm = new LogicPanelViewModel(clock);
        vm.Configure(canvas);

        await vm.BuildNetworkCommand.ExecuteAsync(null);

        vm.HasNetwork.ShouldBeTrue(vm.StatusText);
        vm.HasRegisters.ShouldBeFalse();
        vm.ToggleRunCommand.CanExecute(null).ShouldBeFalse(
            "clocking a purely combinational network is meaningless — Run stays disabled");
        vm.ToggleRunCommand.Execute(null);
        vm.IsRunning.ShouldBeFalse();
        clock.IsStarted.ShouldBeFalse("the clock never starts for a combinational network");
    }

    [Fact]
    public async Task RunMode_RebuildWhileRunning_StopsAutoClock()
    {
        var canvas = RingCanvas();
        var clock = new FakeLogicRunClock();
        var vm = new LogicPanelViewModel(clock);
        vm.Configure(canvas);
        await vm.BuildNetworkCommand.ExecuteAsync(null);
        vm.ToggleRunCommand.Execute(null);
        vm.IsRunning.ShouldBeTrue();

        await vm.BuildNetworkCommand.ExecuteAsync(null);

        vm.IsRunning.ShouldBeFalse("rebuilding the network stops the auto-clock");
        clock.IsStarted.ShouldBeFalse();
        clock.StopCount.ShouldBe(1);
        vm.HasNetwork.ShouldBeTrue(vm.StatusText);
    }

    [Fact]
    public async Task RunMode_DesignEditWhileRunning_StopsAutoClockAndNetwork()
    {
        var canvas = RingCanvas();
        var clock = new FakeLogicRunClock();
        var vm = new LogicPanelViewModel(clock);
        vm.Configure(canvas);
        await vm.BuildNetworkCommand.ExecuteAsync(null);
        vm.ToggleRunCommand.Execute(null);

        canvas.Components.Add(new ComponentViewModel(OrGate("OR1", isRegister: false)));

        vm.IsRunning.ShouldBeFalse("a design edit invalidates the shown network and stops the auto-clock");
        clock.IsStarted.ShouldBeFalse();
        vm.HasNetwork.ShouldBeFalse();
    }

    [Fact]
    public async Task RunMode_IntervalSelection_ForwardsRequestedPeriodToClock()
    {
        var canvas = RingCanvas();
        var clock = new FakeLogicRunClock();
        var vm = new LogicPanelViewModel(clock);
        vm.Configure(canvas);
        await vm.BuildNetworkCommand.ExecuteAsync(null);

        vm.RunIntervalOptions.Select(option => option.Interval).ShouldBe(new[]
        {
            TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2),
        });
        vm.SelectedRunInterval.Interval.ShouldBe(TimeSpan.FromSeconds(1), "the default cadence is 1 s per clock");

        vm.SelectedRunInterval = vm.RunIntervalOptions[2];
        clock.StartedIntervals.ShouldBeEmpty("selecting a cadence before Run does not start the clock");
        vm.ToggleRunCommand.Execute(null);
        clock.StartedIntervals.ToArray().ShouldBe(new[] { TimeSpan.FromSeconds(2) },
            "Run requests exactly the selected period");

        vm.SelectedRunInterval = vm.RunIntervalOptions[0];
        clock.StartedIntervals.ToArray().ShouldBe(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(500) },
            "changing the cadence mid-run re-arms the clock at the new rate");
    }

    [Fact]
    public async Task RunMode_TickAfterStop_IsIgnored()
    {
        var canvas = RingCanvas();
        var clock = new FakeLogicRunClock();
        var vm = new LogicPanelViewModel(clock);
        vm.Configure(canvas);
        await vm.BuildNetworkCommand.ExecuteAsync(null);
        vm.Inputs.Single(i => i.PinName == "R1.b").IsOn = true;

        vm.ToggleRunCommand.Execute(null);
        clock.FireTick();
        RingState(vm).ShouldBe((true, false));

        vm.ToggleRunCommand.Execute(null);
        vm.IsRunning.ShouldBeFalse("toggling again stops the auto-clock");
        clock.FireTick();

        RingState(vm).ShouldBe((true, false), "a tick queued before Stop must not clock the network");
    }

    [Fact]
    public async Task RunMode_InputToggleMidRun_AffectsNextTick()
    {
        var canvas = RingCanvas();
        var clock = new FakeLogicRunClock();
        var vm = new LogicPanelViewModel(clock);
        vm.Configure(canvas);
        await vm.BuildNetworkCommand.ExecuteAsync(null);
        vm.Inputs.Single(i => i.PinName == "R1.b").IsOn = true;

        vm.ToggleRunCommand.Execute(null);
        clock.FireTick();
        RingState(vm).ShouldBe((true, false), "first tick: R1 committed the seeded 1");

        vm.Inputs.Single(i => i.PinName == "R1.b").IsOn = false;
        vm.IsRunning.ShouldBeTrue("flipping an input does not stop the run");
        clock.FireTick();

        RingState(vm).ShouldBe((false, true),
            "the next tick samples the input the user actually sees, and the 1 moved on to R2");
    }

    /// <summary>The committed outputs, register readout and badge bits as one comparable string.</summary>
    private static string SnapshotOf(LogicPanelViewModel vm, DesignCanvasViewModel canvas) =>
        string.Join("|", vm.Outputs.Select(o => $"{o.PinName}={(o.IsOne ? 1 : 0)}"))
        + "||" + string.Join("|", vm.RegisterStates.Select(r => $"{r.GateName}:{r.BitsText}"))
        + "||" + string.Join("|", canvas.LogicGateStates.Badges.Select(b => $"{b.GroupName}.{b.PinName}={(b.IsOne ? 1 : 0)}"));

    /// <summary>The (R1.y, R2.y) committed bits as the panel's output list shows them.</summary>
    private static (bool R1, bool R2) RingState(LogicPanelViewModel vm) =>
        (vm.Outputs.Single(o => o.PinName == "R1.y").IsOne, vm.Outputs.Single(o => o.PinName == "R2.y").IsOne);

    /// <summary>
    /// Manually fired <see cref="ILogicRunClock"/>: records every requested period and
    /// only ticks when the test says so — no wall-clock involved.
    /// </summary>
    private sealed class FakeLogicRunClock : ILogicRunClock
    {
        public List<TimeSpan> StartedIntervals { get; } = new();
        public int StopCount { get; private set; }
        public bool IsStarted { get; private set; }

        public event EventHandler? Tick;

        public void Start(TimeSpan interval)
        {
            StartedIntervals.Add(interval);
            IsStarted = true;
        }

        public void Stop()
        {
            StopCount++;
            IsStarted = false;
        }

        public void FireTick() => Tick?.Invoke(this, EventArgs.Empty);
    }
}
