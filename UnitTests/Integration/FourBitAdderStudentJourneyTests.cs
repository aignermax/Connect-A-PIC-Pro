using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Analysis.LogicAnalysis;
using CAP_Core.Components.Core;
using Shouldly;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// The rung 4→5 student journey over the shipped <c>examples/Logic Gate 4-Bit Adder.lun</c>
/// (issue #1057): the combined surface of the three Logic-panel features that merged
/// pairwise-conflicted in <see cref="LogicPanelViewModel"/> — output signal names
/// (#1046), event timeline (#1045) and named canvas badges (#1051). The journey loads
/// the example through the real load path, builds the logic network with the real
/// <see cref="LogicNetworkAssembler"/>, and pins what the student sees: nine named input
/// toggles, output taps reading S0–S3/Cout, an output badge per gate — named for the
/// five pinned sum/carry taps (issue #1067), anonymous elsewhere — plus a named chip
/// per operand pin, and — the new cross-feature assertion — a toggle whose timeline
/// event set matches exactly the output badges whose bit flipped, with the last event
/// no later than the critical path. Toggling back to zero shows the falling events.
/// Fixture-per-test isolation keeps each step deterministic.
/// </summary>
public class FourBitAdderStudentJourneyTests
    : IClassFixture<LogicGateFourBitAdderExampleTests.FourBitAdderFixture>
{
    /// <summary>The nine operand signals of the 4-bit adder (issues #1025/#1034).</summary>
    private static readonly string[] NetworkInputs =
        { "A0", "A1", "A2", "A3", "B0", "B1", "B2", "B3", "Cin" };

    /// <summary>The named output taps and their raw <c>&lt;gate&gt;.&lt;pin&gt;</c> tooltip targets (#1046).</summary>
    private static readonly Dictionary<string, string> NamedOutputs = new()
    {
        ["S0"] = "T0H2SUM.Y", ["S1"] = "T1H2SUM.Y", ["S2"] = "T2H2SUM.Y",
        ["S3"] = "T3H2SUM.Y", ["Cout"] = "T3OROUT.Y",
    };

    private readonly LogicGateFourBitAdderExampleTests.FourBitAdderFixture _fixture;

    /// <summary>Attaches the shared 4-bit-adder fixture.</summary>
    public FourBitAdderStudentJourneyTests(
        LogicGateFourBitAdderExampleTests.FourBitAdderFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Step1_BuildNetwork_NineNamedInputs_AndSignalNamedOutputs()
    {
        var vm = await BuildPanel();

        vm.Inputs.Select(i => i.PinName).ShouldBe(NetworkInputs, ignoreOrder: true,
            customMessage: "the 261 operand pins merge into exactly the nine named toggles");
        foreach (var (signalName, rawPin) in NamedOutputs)
        {
            vm.Outputs.ShouldContain(o => o.PinName == signalName,
                $"output '{signalName}' reads under its signal name (#1046)");
            vm.Outputs.Single(o => o.PinName == signalName).RawPinName.ShouldBe(rawPin,
                $"the raw <gate>.<pin> id of '{signalName}' only rides the tooltip");
        }
        vm.Inputs.ShouldAllBe(i => !i.IsOn, "all toggles start off");
        vm.Outputs.Where(o => NamedOutputs.ContainsKey(o.PinName))
            .ShouldAllBe(o => !o.IsOne, "0 + 0 + Cin 0 sums to zero on the named outputs");
    }

    [Fact]
    public async Task Step2_CanvasBadges_OneOutputChipPerGate_PlusNamedOperandChips()
    {
        var vm = await BuildPanel();

        var badges = _fixture.Canvas.LogicGateStates.Badges;
        var groups = _fixture.Groups;
        foreach (var group in groups)
        {
            var owner = badges.Where(b => b.GroupName == group.GroupName).ToList();
            var expectedNamed = group.TruthTablePinAssignment?.InputSignalNames;
            var expectedCount = 1 + (expectedNamed?.Count ?? 0);
            owner.Count.ShouldBe(expectedCount,
                $"gate '{group.GroupName}': one output chip plus a named chip per named pin");
            var outputChip = owner.Single(b => b.PinName == "Y");
            var expectedOutputName = group.TruthTablePinAssignment?.OutputSignalNames?.GetValueOrDefault("Y");
            outputChip.SignalName.ShouldBe(expectedOutputName,
                $"the output chip of '{group.GroupName}' names its pinned tap (#1067) or stays anonymous");
            if (expectedNamed != null)
            {
                owner.Where(b => b.PinName != "Y").Select(b => b.SignalName)
                    .ShouldBe(expectedNamed.Values, ignoreOrder: true,
                        customMessage: $"the named operand chips of '{group.GroupName}' mirror the persisted roles");
            }
        }
        badges.Where(b => b.HasSignalName).ShouldAllBe(b =>
            !b.IsOne && b.LabelText == $"{b.SignalName} = 0",
                "with all inputs off every named chip — operand or named output — reads its zero");
        AssertBadgesMirrorPanelOutputs(vm);
    }

    [Fact]
    public async Task Step3_ToggleA0AndB0_OutputsMatchIntegerAddition_NamedBadgesShowOne()
    {
        var vm = await BuildPanel();
        try
        {
            vm.Inputs.Single(i => i.PinName == "A0").IsOn = true;
            vm.Inputs.Single(i => i.PinName == "B0").IsOn = true;

            const int a = 1, b = 1;
            var sum = a + b;
            for (var stage = 0; stage < 4; stage++)
            {
                var expectedBit = ((sum >> stage) & 1) == 1;
                vm.Outputs.Single(o => o.PinName == $"S{stage}").IsOne.ShouldBe(expectedBit,
                    $"S{stage} of {a} + {b} = {sum} (integer addition, not per-pin truth tables)");
            }
            vm.Outputs.Single(o => o.PinName == "Cout").IsOne.ShouldBe(sum > 15,
                "Cout of the 5-bit sum");

            var named = _fixture.Canvas.LogicGateStates.Badges
                .Where(badge => badge.SignalName is "A0" or "B0").ToList();
            named.ShouldNotBeEmpty("many gates read the A0/B0 operand wires");
            named.ShouldAllBe(badge => badge.IsOne && badge.LabelText == $"{badge.SignalName} = 1",
                "every A0/B0 chip on the canvas shows '= 1'");
        }
        finally
        {
            ResetAllInputs(vm);
        }
    }

    [Fact]
    public async Task Step4_ToggleB0_TimelineTimeOrdered_AndMatchesFlippedBadgesExactly()
    {
        var vm = await BuildPanel();
        try
        {
            vm.Inputs.Single(i => i.PinName == "A0").IsOn = true;
            var before = OutputBadges().ToDictionary(b => (b.GroupName, b.PinName), b => b.IsOne);
            vm.Inputs.Single(i => i.PinName == "B0").IsOn = true;

            // The S0/S1 chips carry signal names since #1067; the timeline still matches
            // the flipped output chips — named or anonymous — exactly.
            var changed = _fixture.Canvas.LogicGateStates.Badges
                .Where(b => before.TryGetValue((b.GroupName, b.PinName), out var was) && was != b.IsOne)
                .Select(b => (b.GroupName, b.PinName))
                .ToHashSet();
            var timelinePairs = vm.TimelineEvents.Select(e => (e.Event.GateId, e.Event.OutputPin));
            changed.ShouldBe(timelinePairs.ToHashSet(), ignoreOrder: true,
                "the timeline's switched gates match exactly the output badges whose bit flipped — " +
                "badge chips and event rows must never contradict each other on screen");

            var times = vm.TimelineEvents.Select(e => e.Event.TimePicoseconds).ToList();
            times.ShouldBe(times.OrderBy(t => t).ToList(), "rows arrive in time order");
            vm.TimelineEvents[^1].Event.TimePicoseconds.ShouldBeLessThanOrEqualTo(
                _fixture.Network.CriticalPathDelayPicoseconds,
                "no event lands later than the critical path");
        }
        finally
        {
            ResetAllInputs(vm);
        }
    }

    [Fact]
    public async Task Step5_ToggleBackToZero_OutputsAndBadgesReset_TimelineShowsFallingEvents()
    {
        var vm = await BuildPanel();
        try
        {
            vm.Inputs.Single(i => i.PinName == "A0").IsOn = true;
            vm.Inputs.Single(i => i.PinName == "B0").IsOn = true;

            vm.Inputs.Single(i => i.PinName == "B0").IsOn = false;
            vm.Outputs.Single(o => o.PinName == "S0").IsOne.ShouldBeTrue("A0 alone sums to 1");
            vm.Outputs.Where(o => o.PinName is "S1" or "S2" or "S3" or "Cout")
                .ShouldAllBe(o => !o.IsOne, "only S0 stays on with A0 = 1");

            ResetAllInputs(vm);
        }
        finally
        {
            ResetAllInputs(vm);
        }
        vm.Outputs.Where(o => NamedOutputs.ContainsKey(o.PinName))
            .ShouldAllBe(o => !o.IsOne, "all inputs back to zero — the named outputs return to 0");
        _fixture.Canvas.LogicGateStates.Badges.Where(b => b.HasSignalName)
            .ShouldAllBe(b => !b.IsOne, "every named chip reads 0 again");
        AssertBadgesMirrorPanelOutputs(vm);
        vm.TimelineEvents.ShouldContain(e => !e.Event.NewValue,
            "the last toggle leaves its 1→0 events on the timeline");
    }

    /// <summary>
    /// The output-pin badges currently on the canvas, materialized: every chip sitting
    /// on a gate's output tap — anonymous or named (the S0–S3/Cout chips of #1067).
    /// Named input chips (issue #1051) carry their signal's live bit, not a gate
    /// output bit, so tap mirroring steps around them.
    /// </summary>
    private List<LogicGateBadgeViewModel> OutputBadges() =>
        _fixture.Canvas.LogicGateStates.Badges
            .Where(b => _fixture.Network.OutputTaps.Values.Any(
                p => p.GateId == b.GroupName && p.PinName == b.PinName))
            .ToList();

    /// <summary>
    /// Every output badge must carry the same bit the panel's output list shows for
    /// its tapped pin — chips and output rows must never disagree on screen, whether
    /// the chip reads a signal name or stays anonymous.
    /// </summary>
    private void AssertBadgesMirrorPanelOutputs(LogicPanelViewModel vm)
    {
        var outputsByRawPin = vm.Outputs.ToDictionary(o => o.RawPinName, o => o);
        foreach (var badge in OutputBadges())
        {
            var raw = $"{badge.GroupName}.{badge.PinName}";
            outputsByRawPin.TryGetValue(raw, out var output).ShouldBeTrue(
                $"badge '{raw}' taps a pin the panel output list must expose");
            badge.IsOne.ShouldBe(output!.IsOne,
                $"badge '{raw}' mirrors the panel's output bit");
        }

        var namedBySignal = vm.Inputs.ToDictionary(i => i.PinName, i => i.IsOn);
        foreach (var badge in _fixture.Canvas.LogicGateStates.Badges
                     .Where(b => b.HasSignalName && NetworkInputs.Contains(b.SignalName)))
        {
            badge.IsOne.ShouldBe(namedBySignal[badge.SignalName!],
                $"named chip '{badge.SignalName}' on '{badge.GroupName}' mirrors its toggle bit");
        }
    }

    /// <summary>Builds a fresh panel on the shared loaded canvas; fails fast on assembly errors.</summary>
    private async Task<LogicPanelViewModel> BuildPanel()
    {
        var vm = new LogicPanelViewModel();
        vm.Configure(_fixture.Canvas);
        await vm.BuildNetworkCommand.ExecuteAsync(null);
        vm.HasNetwork.ShouldBeTrue(vm.StatusText);
        return vm;
    }

    /// <summary>Drives every toggle of the panel back to zero (fixture hygiene).</summary>
    private static void ResetAllInputs(LogicPanelViewModel vm)
    {
        foreach (var input in vm.Inputs)
            input.IsOn = false;
    }
}
