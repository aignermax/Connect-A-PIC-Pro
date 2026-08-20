using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CAP.Avalonia.ViewModels.Canvas;
using Shouldly;
using Xunit;
using static UnitTests.Helpers.LogicRingTestFixture;

namespace UnitTests.Integration;

/// <summary>
/// ViewModel tests for the Logic panel's clock Step button and register-state
/// readout (issue #1099, rung 5): the evaluator's <see
/// cref="CAP_Core.Analysis.LogicAnalysis.LogicNetworkEvaluator.Step"/> becomes
/// reachable from the UI. The pinned circuit is a two-register ring (R1.y → R2.a,
/// R2.y → R1.a) built from the combiner fixture — the physically honest stand-in
/// for the toggle loop <c>reg = NOT(reg)</c>, since the passive fixture gates
/// cannot invert: once seeded, each register samples the other's committed output,
/// so every press flips every register exactly once. A purely combinational
/// network hides and disables the button.
/// </summary>
public class LogicPanelStepClockTests
{
    [Fact]
    public async Task StepClock_TwoRegisterRing_FlipsEachRegisterOncePerPress()
    {
        var canvas = RingCanvas();
        var vm = new LogicPanelViewModel();
        vm.Configure(canvas);
        await vm.BuildNetworkCommand.ExecuteAsync(null);
        vm.HasNetwork.ShouldBeTrue(vm.StatusText);

        vm.HasRegisters.ShouldBeTrue("both ring gates are designated registers");
        vm.StepClockCommand.CanExecute(null).ShouldBeTrue();
        OutputBit(vm, "R1.y").ShouldBeFalse("registers power up cleared");
        OutputBit(vm, "R2.y").ShouldBeFalse();

        // Seed the ring through R1's free input: the outputs hold — no clock, no commit.
        vm.Inputs.Single(i => i.PinName == "R1.b").IsOn = true;
        OutputBit(vm, "R1.y").ShouldBeFalse("a settled input is not committed without a clock step");

        vm.StepClockCommand.Execute(null);
        RingState(vm).ShouldBe((true, false), "first clock: R1 committed the seeded 1, R2 sampled R1's old 0");

        vm.Inputs.Single(i => i.PinName == "R1.b").IsOn = false;
        OutputBit(vm, "R1.y").ShouldBeTrue("the committed bit holds while the input settles low");

        vm.StepClockCommand.Execute(null);
        RingState(vm).ShouldBe((false, true), "second clock: the 1 moved on to R2");

        vm.StepClockCommand.Execute(null);
        RingState(vm).ShouldBe((true, false), "third clock: the 1 completes the loop — one flip per press");

        vm.StepClockCommand.Execute(null);
        RingState(vm).ShouldBe((false, true), "fourth clock: every press flips every register exactly once");
    }

    [Fact]
    public async Task StepClock_PurelyCombinationalNetwork_IsDisabledWithNoRegisterRows()
    {
        var canvas = new DesignCanvasViewModel();
        canvas.Components.Add(new ComponentViewModel(OrGate("OR1", isRegister: false)));
        var vm = new LogicPanelViewModel();
        vm.Configure(canvas);

        await vm.BuildNetworkCommand.ExecuteAsync(null);

        vm.HasNetwork.ShouldBeTrue(vm.StatusText);
        vm.HasRegisters.ShouldBeFalse("no gate of the network is a designated register");
        vm.StepClockCommand.CanExecute(null).ShouldBeFalse(
            "clocking a purely combinational network is meaningless — the button stays disabled");
        vm.RegisterStates.ShouldBeEmpty();
    }

    [Fact]
    public async Task StepClock_CommittedBits_RefreshRegisterReadoutAndCanvasBadges()
    {
        var canvas = RingCanvas();
        var vm = new LogicPanelViewModel();
        vm.Configure(canvas);
        await vm.BuildNetworkCommand.ExecuteAsync(null);
        vm.HasNetwork.ShouldBeTrue(vm.StatusText);

        vm.RegisterStates.Select(r => r.GateName).ShouldBe(new[] { "R1", "R2" },
            "one readout row per register gate, in gate-name order");
        vm.RegisterStates.ShouldAllBe(r => r.BitsText == "y = 0");

        vm.Inputs.Single(i => i.PinName == "R1.b").IsOn = true;
        vm.StepClockCommand.Execute(null);

        vm.RegisterStates.Single(r => r.GateName == "R1").BitsText.ShouldBe("y = 1");
        vm.RegisterStates.Single(r => r.GateName == "R2").BitsText.ShouldBe("y = 0");
        BadgeBit(canvas, "R1", "y").ShouldBeTrue("the canvas badges re-settled with the new state");
        BadgeBit(canvas, "R2", "y").ShouldBeFalse();

        vm.Inputs.Single(i => i.PinName == "R1.b").IsOn = false;
        vm.StepClockCommand.Execute(null);

        vm.RegisterStates.Single(r => r.GateName == "R1").BitsText.ShouldBe("y = 0");
        vm.RegisterStates.Single(r => r.GateName == "R2").BitsText.ShouldBe("y = 1");
        BadgeBit(canvas, "R1", "y").ShouldBeFalse();
        BadgeBit(canvas, "R2", "y").ShouldBeTrue();
    }

    /// <summary>The (R1.y, R2.y) committed bits as the panel's output list shows them.</summary>
    private static (bool R1, bool R2) RingState(LogicPanelViewModel vm) =>
        (OutputBit(vm, "R1.y"), OutputBit(vm, "R2.y"));

    /// <summary>The live bit of one network output tap.</summary>
    private static bool OutputBit(LogicPanelViewModel vm, string tapName) =>
        vm.Outputs.Single(o => o.PinName == tapName).IsOne;

    /// <summary>The bit of one canvas badge, addressed by gate group and pin.</summary>
    private static bool BadgeBit(DesignCanvasViewModel canvas, string groupName, string pinName) =>
        canvas.LogicGateStates.Badges.Single(b => b.GroupName == groupName && b.PinName == pinName).IsOne;
}
