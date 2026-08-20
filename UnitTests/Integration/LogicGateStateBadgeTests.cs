using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Core;
using Shouldly;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// ViewModel tests for the canvas logic-state overlay (issue #994, rung 4 of the NAND
/// game): while the Logic panel holds a built network, every gate group of the shipped
/// <c>examples/Logic Gate Half Adder.lun</c> carries its live 0/1 badge on the canvas —
/// the same table-lookup data the panel's output list shows. Toggling the addend signals
/// A and B (issue #1025) flips the Sum badge (<c>NAND4</c>) and the Carry badge
/// (<c>NOT1</c>) for all four input combinations; a design edit discards the network and
/// clears every badge, and a fresh build re-populates them without duplicating.
/// Gate input pins carrying a persisted signal name additionally show the name next to
/// the live bit (<c>A = 1</c>, issue #1051); removing the name drops the label on the
/// next build, and unnamed pins keep the plain 0/1 chip exactly.
/// </summary>
public class LogicGateStateBadgeTests : IClassFixture<LogicPanelViewModelTests.LoadedHalfAdder>
{
    /// <summary>The gate groups of the half-adder example, each with its single output pin Y.</summary>
    private static readonly string[] GateNames =
        { "NAND1A", "NAND1B", "NAND2", "NAND3", "NAND4", "NAND5", "NOT1" };

    /// <summary>Named input badges per gate group (issue #1051): the persisted signal
    /// names A and B (issue #1025) on the group's input pins; gates without named pins
    /// (<c>NAND4</c>, <c>NOT1</c>) carry no named badge.</summary>
    private static readonly Dictionary<string, string[]> ExpectedSignalNamesByGate = new()
    {
        ["NAND1A"] = new[] { "A", "B" },
        ["NAND1B"] = new[] { "A", "B" },
        ["NAND2"] = new[] { "A" },
        ["NAND3"] = new[] { "B" },
        ["NAND4"] = System.Array.Empty<string>(),
        ["NAND5"] = new[] { "A", "B" },
        ["NOT1"] = System.Array.Empty<string>(),
    };

    private readonly LogicPanelViewModelTests.LoadedHalfAdder _fixture;

    /// <summary>Attaches the shared loaded half-adder canvas.</summary>
    public LogicGateStateBadgeTests(LogicPanelViewModelTests.LoadedHalfAdder fixture) => _fixture = fixture;

    [Fact]
    public async Task BuildNetwork_HalfAdder_ShowsOneBadgePerGateGroup()
    {
        var (vm, canvas) = await BuildOnFixtureCanvas();

        var outputBadges = canvas.LogicGateStates.Badges.Where(b => !b.HasSignalName).ToList();
        outputBadges.Count.ShouldBe(GateNames.Length,
            "every gate group carries exactly one anonymous badge for its output pin Y");
        outputBadges.Select(b => b.GroupName).ShouldBe(GateNames, ignoreOrder: true);
        outputBadges.ShouldAllBe(b => b.PinName == "Y" && b.LabelText == b.BitText);
        foreach (var badge in outputBadges)
        {
            var tapName = $"{badge.GroupName}.{badge.PinName}";
            badge.IsOne.ShouldBe(vm.Outputs.Single(o => o.PinName == tapName).IsOne,
                $"badge '{tapName}' must mirror the panel's output list");
            badge.BitText.ShouldBe(badge.IsOne ? "1" : "0");
        }
    }

    [Fact]
    public async Task BuildNetwork_HalfAdder_NamedInputPins_ShowSignalNameBadges()
    {
        // Issue #1051: a gate whose input pins carry a persisted signal name shows the
        // name next to the live bit — "A = 0" — so the canvas reads like the circuit.
        var (_, canvas) = await BuildOnFixtureCanvas();

        foreach (var gateName in GateNames)
        {
            var named = canvas.LogicGateStates.Badges
                .Where(b => b.GroupName == gateName && b.HasSignalName).ToList();
            named.Select(b => b.SignalName).ShouldBe(ExpectedSignalNamesByGate[gateName], ignoreOrder: true,
                customMessage: $"group '{gateName}' shows one named badge per named input pin");
            foreach (var badge in named)
            {
                badge.IsOne.ShouldBeFalse("all inputs start off");
                badge.LabelText.ShouldBe($"{badge.SignalName} = 0");
            }
        }
    }

    [Fact]
    public async Task ToggleInputs_HalfAdder_NamedBadgesTrackTheirSignalBit()
    {
        var (vm, canvas) = await BuildOnFixtureCanvas();

        foreach (var (a, b) in new[] { (false, false), (true, false), (false, true), (true, true) })
        {
            vm.Inputs.Single(i => i.PinName == "A").IsOn = a;
            vm.Inputs.Single(i => i.PinName == "B").IsOn = b;

            foreach (var badge in canvas.LogicGateStates.Badges.Where(badge => badge.HasSignalName))
            {
                var expected = badge.SignalName == "A" ? a : b;
                badge.IsOne.ShouldBe(expected,
                    $"the '{badge.SignalName}' badge on '{badge.GroupName}' mirrors the signal's live bit");
                badge.LabelText.ShouldBe($"{badge.SignalName} = {(expected ? "1" : "0")}");
            }
        }
    }

    [Fact]
    public async Task SignalNameRemoved_HalfAdder_RebuildDropsTheLabel()
    {
        // Issue #1051: the badge mirrors the persisted assignment — remove a pin's
        // signal name and the next build shows the gate's anonymous badge only.
        var group = _fixture.Canvas.Components
            .Select(c => c.Component).OfType<ComponentGroup>()
            .Single(g => g.GroupName == "NAND2");
        var persisted = group.TruthTablePinAssignment!.InputSignalNames;
        persisted.ShouldNotBeNull("NAND2 ships its addend-A name on pin A");
        group.TruthTablePinAssignment.InputSignalNames = null;
        try
        {
            var (_, canvas) = await BuildOnFixtureCanvas();

            var badges = canvas.LogicGateStates.Badges.Where(b => b.GroupName == "NAND2").ToList();
            badges.Count.ShouldBe(1, "without the persisted name the gate keeps its plain output badge");
            badges.Single().HasSignalName.ShouldBeFalse();
            badges.Single().LabelText.ShouldBe(badges.Single().BitText);
            canvas.LogicGateStates.Badges.Where(b => b.HasSignalName).Select(b => b.SignalName)
                .Distinct().ShouldBe(new[] { "A", "B" }, ignoreOrder: true,
                    customMessage: "the other gates keep their named badges");
        }
        finally
        {
            group.TruthTablePinAssignment.InputSignalNames = persisted;
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
            vm.Inputs.Single(i => i.PinName == "A").IsOn = a;
            vm.Inputs.Single(i => i.PinName == "B").IsOn = b;

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
        var expectedCount = GateNames.Length + ExpectedSignalNamesByGate.Values.Sum(names => names.Length);
        canvas.LogicGateStates.Badges.Count.ShouldBe(expectedCount,
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
}
