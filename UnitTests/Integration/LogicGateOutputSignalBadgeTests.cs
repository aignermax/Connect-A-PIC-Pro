using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Core;
using Shouldly;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Unit part of the named output-chip tests (issue #1067, rung 4→5 of the NAND game,
/// symmetric to the named input chips of issue #1051): over the shared loaded half-adder
/// canvas one gate's persisted roles gain an output signal name, and
/// <c>LogicPanelViewModel.BadgeStatesOf</c> must attach that name to the tap's badge
/// state (<c>S = 1</c> instead of the bare bit) while every raw
/// <c>&lt;gate&gt;.&lt;pin&gt;</c> tap stays anonymous. The data is already on the wire:
/// the evaluation result keys by tap name, so the badge walk attaches the name whenever
/// the tap key differs from the raw pin id.
/// </summary>
public class LogicGateOutputSignalBadgeUnitTests : IClassFixture<LogicPanelViewModelTests.LoadedHalfAdder>
{
    private readonly LogicPanelViewModelTests.LoadedHalfAdder _fixture;

    /// <summary>Attaches the shared loaded half-adder canvas.</summary>
    public LogicGateOutputSignalBadgeUnitTests(LogicPanelViewModelTests.LoadedHalfAdder fixture) =>
        _fixture = fixture;

    [Fact]
    public async Task BuildNetwork_NamedOutputTapChipsItsName_RawTapsStayAnonymous()
    {
        var group = _fixture.Canvas.Components
            .Select(c => c.Component).OfType<ComponentGroup>()
            .Single(g => g.GroupName == "NAND4");
        var persisted = group.TruthTablePinAssignment!.OutputSignalNames;
        group.TruthTablePinAssignment!.OutputSignalNames =
            new Dictionary<string, string> { ["Y"] = "S" };
        try
        {
            var vm = new LogicPanelViewModel();
            vm.Configure(_fixture.Canvas);
            await vm.BuildNetworkCommand.ExecuteAsync(null);
            vm.HasNetwork.ShouldBeTrue(vm.StatusText);

            var chip = _fixture.Canvas.LogicGateStates.Badges
                .Single(b => b.GroupName == "NAND4" && b.PinName == "Y");
            chip.HasSignalName.ShouldBeTrue("the named tap chips its signal name, not the bare bit");
            chip.SignalName.ShouldBe("S");
            chip.LabelText.ShouldBe("S = 0", "the half adder starts with its sum off");
            chip.IsOne.ShouldBe(vm.Outputs.Single(o => o.PinName == "S").IsOne,
                "the chip mirrors the panel's named output row");

            var anonymous = _fixture.Canvas.LogicGateStates.Badges.Where(b => !b.HasSignalName).ToList();
            anonymous.ShouldAllBe(b => b.PinName == "Y" && b.LabelText == b.BitText,
                "raw <gate>.<pin> taps keep the exact anonymous chip");
            anonymous.ShouldNotContain(b => b.GroupName == "NAND4" && b.PinName == "Y",
                "only the named tap lost its anonymity");

            vm.Inputs.Single(i => i.PinName == "A").IsOn = true;
            _fixture.Canvas.LogicGateStates.Badges.Single(b => b.SignalName == "S")
                .IsOne.ShouldBeTrue("S = A XOR B");
            _fixture.Canvas.LogicGateStates.Badges.Single(b => b.SignalName == "S")
                .LabelText.ShouldBe("S = 1");
            vm.Inputs.Single(i => i.PinName == "A").IsOn = false;
        }
        finally
        {
            group.TruthTablePinAssignment!.OutputSignalNames = persisted;
        }
    }
}

/// <summary>
/// Integration part of the named output-chip tests (issue #1067): over the shipped
/// <c>examples/Logic Gate 4-Bit Adder.lun</c> — real load path, real build — the gates
/// behind the network's named taps <c>S0</c>–<c>S3</c>/<c>Cout</c> carry named chips
/// mirroring the panel's output rows, toggling <c>A0</c>/<c>B0</c> flips the
/// <c>S0</c> chip, and the unnamed intermediate gates stay exactly anonymous.
/// </summary>
public class LogicGateOutputSignalBadgeTests
    : IClassFixture<LogicGateFourBitAdderExampleTests.FourBitAdderFixture>
{
    /// <summary>The named output taps of the 4-bit adder (#1046), per tapped gate.</summary>
    private static readonly Dictionary<string, string> NamedOutputsByGate = new()
    {
        ["T0H2SUM"] = "S0", ["T1H2SUM"] = "S1", ["T2H2SUM"] = "S2",
        ["T3H2SUM"] = "S3", ["T3OROUT"] = "Cout",
    };

    private readonly LogicGateFourBitAdderExampleTests.FourBitAdderFixture _fixture;

    /// <summary>Attaches the shared 4-bit-adder fixture.</summary>
    public LogicGateOutputSignalBadgeTests(LogicGateFourBitAdderExampleTests.FourBitAdderFixture fixture) =>
        _fixture = fixture;

    [Fact]
    public async Task BuildNetwork_FourBitAdder_SumAndCarryGatesCarryNamedChips()
    {
        var vm = await BuildPanel();

        foreach (var (gate, signal) in NamedOutputsByGate)
        {
            var chip = ChipOf(gate);
            chip.SignalName.ShouldBe(signal,
                $"the gate '{gate}' behind the named tap chips its persisted output signal name");
            chip.LabelText.ShouldBe($"{signal} = 0", "0 + 0 + Cin 0 sums to zero");
            chip.IsOne.ShouldBe(vm.Outputs.Single(o => o.PinName == signal).IsOne,
                $"the chip of '{gate}' mirrors the panel's named output row");
        }

        var anonymous = _fixture.Canvas.LogicGateStates.Badges.Where(b => !b.HasSignalName).ToList();
        anonymous.Select(b => b.GroupName).Distinct().ShouldAllBe(
            gate => !NamedOutputsByGate.ContainsKey(gate),
            "only the five named output taps lose their anonymity");
        anonymous.ShouldAllBe(b => b.PinName == "Y" && b.LabelText == b.BitText,
            "raw <gate>.<pin> taps keep the exact anonymous chip — no visual change");
        var intermediate = anonymous.Single(b => b.GroupName == "T1H1SUM1");
        intermediate.HasSignalName.ShouldBeFalse(
            "an unnamed intermediate gate of the sum ladder stays anonymous");
    }

    [Fact]
    public async Task ToggleA0AndB0_FourBitAdder_FlipsTheS0Chip()
    {
        var vm = await BuildPanel();
        try
        {
            ChipOf("T0H2SUM").LabelText.ShouldBe("S0 = 0");

            vm.Inputs.Single(i => i.PinName == "A0").IsOn = true;
            ChipOf("T0H2SUM").IsOne.ShouldBeTrue("A0 = 1 sums to 1 at the low bit");
            ChipOf("T0H2SUM").LabelText.ShouldBe("S0 = 1");

            vm.Inputs.Single(i => i.PinName == "B0").IsOn = true;
            ChipOf("T0H2SUM").IsOne.ShouldBeFalse("1 + 1 carries — the low bit drops to 0");
            ChipOf("T0H2SUM").LabelText.ShouldBe("S0 = 0");
            vm.Outputs.Single(o => o.PinName == "S0").IsOne.ShouldBeFalse(
                "the chip and the panel's named output row agree");
        }
        finally
        {
            vm.Inputs.Single(i => i.PinName == "A0").IsOn = false;
            vm.Inputs.Single(i => i.PinName == "B0").IsOn = false;
        }
    }

    /// <summary>The output chip of one gate group on the badge overlay.</summary>
    private LogicGateBadgeViewModel ChipOf(string gateName) =>
        _fixture.Canvas.LogicGateStates.Badges.Single(b => b.GroupName == gateName && b.PinName == "Y");

    /// <summary>Builds a fresh panel on the shared loaded canvas; fails fast on assembly errors.</summary>
    private async Task<LogicPanelViewModel> BuildPanel()
    {
        var vm = new LogicPanelViewModel();
        vm.Configure(_fixture.Canvas);
        await vm.BuildNetworkCommand.ExecuteAsync(null);
        vm.HasNetwork.ShouldBeTrue(vm.StatusText);
        return vm;
    }
}
