using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CAP.Avalonia.ViewModels.Canvas;
using Shouldly;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// ViewModel tests for the canvas logic-state overlay (issue #994, rung 4 of the NAND
/// game): while the Logic panel holds a built network, every gate group of the shipped
/// <c>examples/Logic Gate Half Adder.lun</c> carries its live 0/1 badge on the canvas —
/// the same table-lookup data the panel's output list shows. Toggling the addends flips
/// the Sum badge (<c>NAND4</c>) and the Carry badge (<c>NOT1</c>) for all four input
/// combinations; a design edit discards the network and clears every badge, and a fresh
/// build re-populates them without duplicating.
/// </summary>
public class LogicGateStateBadgeTests : IClassFixture<LogicPanelViewModelTests.LoadedHalfAdder>
{
    /// <summary>Network inputs driven by addend A (fan-out at the logic layer).</summary>
    private static readonly string[] InputsA = { "NAND1A.A", "NAND1B.A", "NAND2.A", "NAND5.A" };

    /// <summary>Network inputs driven by addend B (fan-out at the logic layer).</summary>
    private static readonly string[] InputsB = { "NAND1A.B", "NAND1B.B", "NAND3.B", "NAND5.B" };

    /// <summary>The gate groups of the half-adder example, each with its single output pin Y.</summary>
    private static readonly string[] GateNames =
        { "NAND1A", "NAND1B", "NAND2", "NAND3", "NAND4", "NAND5", "NOT1" };

    private readonly LogicPanelViewModelTests.LoadedHalfAdder _fixture;

    /// <summary>Attaches the shared loaded half-adder canvas.</summary>
    public LogicGateStateBadgeTests(LogicPanelViewModelTests.LoadedHalfAdder fixture) => _fixture = fixture;

    [Fact]
    public async Task BuildNetwork_HalfAdder_ShowsOneBadgePerGateGroup()
    {
        var (vm, canvas) = await BuildOnFixtureCanvas();

        canvas.LogicGateStates.Badges.Count.ShouldBe(GateNames.Length,
            "every gate group carries exactly one badge for its output pin Y");
        canvas.LogicGateStates.Badges.Select(b => b.GroupName).ShouldBe(GateNames, ignoreOrder: true);
        canvas.LogicGateStates.Badges.ShouldAllBe(b => b.PinName == "Y");
        foreach (var badge in canvas.LogicGateStates.Badges)
        {
            var tapName = $"{badge.GroupName}.{badge.PinName}";
            badge.IsOne.ShouldBe(vm.Outputs.Single(o => o.PinName == tapName).IsOne,
                $"badge '{tapName}' must mirror the panel's output list");
            badge.BitText.ShouldBe(badge.IsOne ? "1" : "0");
        }
    }

    [Fact]
    public async Task ToggleInputs_HalfAdder_BadgesFlipWithSumAndCarry()
    {
        var (vm, canvas) = await BuildOnFixtureCanvas();

        foreach (var (a, b, expectedSum, expectedCarry) in new[]
                 {
                     (false, false, false, false),
                     (true, false, true, false),
                     (false, true, true, false),
                     (true, true, false, true),
                 })
        {
            SetAddend(vm, InputsA, a);
            SetAddend(vm, InputsB, b);

            canvas.LogicGateStates.Badges.Single(badge => badge.GroupName == "NAND4").IsOne
                .ShouldBe(expectedSum, $"Sum badge = A XOR B for A={a}, B={b}");
            canvas.LogicGateStates.Badges.Single(badge => badge.GroupName == "NOT1").IsOne
                .ShouldBe(expectedCarry, $"Carry badge = A AND B for A={a}, B={b}");
        }
    }

    [Fact]
    public async Task DesignEdit_HalfAdder_DiscardsNetworkAndClearsBadges()
    {
        var (vm, canvas) = await BuildOnFixtureCanvas();
        canvas.LogicGateStates.Badges.ShouldNotBeEmpty();

        // Add and remove a probe component, restoring the shared fixture canvas for the
        // other tests; the add alone must discard the network and its badges.
        var probe = new ComponentViewModel(TestComponentFactory.CreateStraightWaveGuide());
        canvas.Components.Add(probe);
        try
        {
            vm.HasNetwork.ShouldBeFalse("a design edit invalidates the built network");
            canvas.LogicGateStates.Badges.ShouldBeEmpty("the badges die with the network");
            vm.StatusText.ShouldNotBeNullOrEmpty("the panel says why the network is gone");
        }
        finally
        {
            canvas.Components.Remove(probe);
        }
    }

    [Fact]
    public async Task Rebuild_HalfAdder_RefreshesBadgesWithoutDuplicating()
    {
        var (vm, canvas) = await BuildOnFixtureCanvas();

        await vm.BuildNetworkCommand.ExecuteAsync(null);

        vm.HasNetwork.ShouldBeTrue(vm.StatusText);
        canvas.LogicGateStates.Badges.Count.ShouldBe(GateNames.Length,
            "a second build replaces the badges instead of stacking another set");
    }

    /// <summary>Builds the network of the shared fixture canvas through a fresh panel VM.</summary>
    private async Task<(LogicPanelViewModel Panel, DesignCanvasViewModel Canvas)> BuildOnFixtureCanvas()
    {
        var vm = new LogicPanelViewModel();
        vm.Configure(_fixture.Canvas);
        await vm.BuildNetworkCommand.ExecuteAsync(null);
        vm.HasNetwork.ShouldBeTrue(vm.StatusText);
        return (vm, _fixture.Canvas);
    }

    /// <summary>Sets every network input pin of one addend to the same bit.</summary>
    private static void SetAddend(LogicPanelViewModel vm, IEnumerable<string> pinNames, bool bit)
    {
        foreach (var name in pinNames)
            vm.Inputs.Single(i => i.PinName == name).IsOn = bit;
    }
}
