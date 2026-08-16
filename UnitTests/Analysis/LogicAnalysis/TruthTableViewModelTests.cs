using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CAP.Avalonia.ViewModels.Canvas;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.LogicAnalysis;

/// <summary>
/// ViewModel tests for the Truth Table panel: activation only for exactly one
/// selected group, the 4-input limit at the checkboxes, pin-role mutual exclusion
/// across input/output/bias at the checkbox, extraction on the
/// <see cref="LogicGateFixtureFactory"/> fixtures (OR table at threshold 0.25 including
/// raw powers), extractor validation shown as a message instead of a crash, and cancel.
/// </summary>
public class TruthTableViewModelTests
{
    private static string Translate(string key) => LocalizationService.Instance.Translate(key);

    private static (TruthTableViewModel vm, ComponentViewModel component) ConfigureForGroup(
        CAP_Core.Components.Core.ComponentGroup group, DesignCanvasViewModel? canvas = null)
    {
        canvas ??= new DesignCanvasViewModel();
        var component = new ComponentViewModel(group);
        canvas.Selection.SelectSingle(component);
        var vm = new TruthTableViewModel();
        vm.ConfigureForSelection(component, canvas);
        return (vm, component);
    }

    private static void CheckPins(TruthTableViewModel vm, string[] inputs, string[] outputs)
    {
        foreach (var name in inputs)
            vm.InputPins.Single(p => p.PinName == name).IsChecked = true;
        foreach (var name in outputs)
            vm.OutputPins.Single(p => p.PinName == name).IsChecked = true;
    }

    private static void CheckBiasPins(TruthTableViewModel vm, params string[] biases)
    {
        foreach (var name in biases)
            vm.BiasPins.Single(p => p.PinName == name).IsChecked = true;
    }

    [Fact]
    public void ConfigureForSelection_NoComponent_IsInactive()
    {
        var vm = new TruthTableViewModel();
        vm.ConfigureForSelection(null, new DesignCanvasViewModel());

        vm.IsGroupSelected.ShouldBeFalse();
        vm.InputPins.ShouldBeEmpty();
        vm.OutputPins.ShouldBeEmpty();
    }

    [Fact]
    public void ConfigureForSelection_NonGroupComponent_IsInactive()
    {
        var canvas = new DesignCanvasViewModel();
        var component = new ComponentViewModel(TestComponentFactory.CreateStraightWaveGuide());
        canvas.Selection.SelectSingle(component);
        var vm = new TruthTableViewModel();
        vm.ConfigureForSelection(component, canvas);

        vm.IsGroupSelected.ShouldBeFalse();
    }

    [Fact]
    public void ConfigureForSelection_SingleGroup_IsActiveAndListsExternalPins()
    {
        var (vm, _) = ConfigureForGroup(LogicGateFixtureFactory.CreateCombinerGroup());

        vm.IsGroupSelected.ShouldBeTrue();
        vm.InputPins.Select(p => p.PinName).ShouldBe(new[] { "a", "b", "y" });
        vm.OutputPins.Select(p => p.PinName).ShouldBe(new[] { "a", "b", "y" });
        vm.BiasPins.Select(p => p.PinName).ShouldBe(new[] { "a", "b", "y" });
        vm.WavelengthText.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ConfigureForSelection_GroupInMultiSelection_IsInactive()
    {
        var canvas = new DesignCanvasViewModel();
        var groupVm = new ComponentViewModel(LogicGateFixtureFactory.CreateCombinerGroup());
        canvas.Selection.SelectSingle(groupVm);
        canvas.Selection.AddToSelection(new ComponentViewModel(TestComponentFactory.CreateStraightWaveGuide()));

        var vm = new TruthTableViewModel();
        vm.ConfigureForSelection(groupVm, canvas);

        vm.IsGroupSelected.ShouldBeFalse("the panel activates only for exactly one selected group");
    }

    [Fact]
    public void InputPins_FifthCheck_IsRevertedWithMessage()
    {
        var (vm, _) = ConfigureForGroup(LogicGateFixtureFactory.CreateFourBitBusGroup());

        foreach (var pin in vm.InputPins.Take(4))
            pin.IsChecked = true;
        var fifth = vm.InputPins.Skip(4).First();
        fifth.IsChecked = true;

        fifth.IsChecked.ShouldBeFalse("the extractor supports at most 4 inputs");
        vm.InputPins.Count(p => p.IsChecked).ShouldBe(4);
        vm.StatusText.ShouldBe(string.Format(Translate("Analysis.TruthTable.TooManyInputs"), 4));
    }

    [Fact]
    public async Task Extract_CombinerGroup_ProducesOrTableWithRawPowers()
    {
        var (vm, _) = ConfigureForGroup(LogicGateFixtureFactory.CreateCombinerGroup());
        CheckPins(vm, new[] { "a", "b" }, new[] { "y" });
        vm.Threshold = 0.25;

        await vm.ExtractCommand.ExecuteAsync(null);

        vm.HasResult.ShouldBeTrue();
        vm.InputHeaders.ShouldBe(new[] { "a", "b" });
        vm.OutputHeaders.ShouldBe(new[] { "y" });
        vm.Rows.Count.ShouldBe(4);

        var row00 = vm.Rows.Single(r => r.InputBitsText == "0 0");
        row00.OutputCells[0].IsOne.ShouldBeFalse();
        row00.OutputCells[0].PowerText.ShouldBe("0.00");

        foreach (var bits in new[] { "1 0", "0 1" })
        {
            var row = vm.Rows.Single(r => r.InputBitsText == bits);
            row.OutputCells[0].IsOne.ShouldBeTrue("0.5 ≥ 0.25 — the OR lesson");
            row.OutputCells[0].PowerText.ShouldBe("0.50");
        }

        var row11 = vm.Rows.Single(r => r.InputBitsText == "1 1");
        row11.OutputCells[0].IsOne.ShouldBeTrue();
        row11.OutputCells[0].PowerText.ShouldBe("1.00", "coherent recombination doubles the field");
    }

    [Fact]
    public async Task Extract_CombinerGroupWithBias_ShowsBiasAssignmentAndInterference()
    {
        var (vm, _) = ConfigureForGroup(LogicGateFixtureFactory.CreateCombinerGroup());
        CheckPins(vm, new[] { "a" }, new[] { "y" });
        vm.BiasPins.Single(p => p.PinName == "b").IsChecked = true;
        vm.Threshold = 0.75;

        await vm.ExtractCommand.ExecuteAsync(null);

        vm.HasResult.ShouldBeTrue();
        vm.InputHeaders.ShouldBe(new[] { "a" }); // the bias pin never becomes an input column
        vm.BiasSummaryText.ShouldBe(string.Format(Translate("TruthTable.BiasSummary"), "b"));
        vm.Rows.Count.ShouldBe(2);

        var resting = vm.Rows.Single(r => r.InputBitsText == "0");
        resting.OutputCells[0].IsOne.ShouldBeFalse("0.5 resting power stays below the 0.75 threshold");
        resting.OutputCells[0].PowerText.ShouldBe("0.50");

        var active = vm.Rows.Single(r => r.InputBitsText == "1");
        active.OutputCells[0].IsOne.ShouldBeTrue("bias and input recombine coherently into full power");
        active.OutputCells[0].PowerText.ShouldBe("1.00");
    }

    [Fact]
    public async Task Extract_WithoutBias_ClearsBiasSummary()
    {
        var (vm, _) = ConfigureForGroup(LogicGateFixtureFactory.CreateCombinerGroup());
        CheckPins(vm, new[] { "a" }, new[] { "y" });
        var bias = vm.BiasPins.Single(p => p.PinName == "b");
        bias.IsChecked = true;
        await vm.ExtractCommand.ExecuteAsync(null);
        vm.BiasSummaryText.ShouldNotBeNullOrWhiteSpace("the bias assignment shows on the result");

        bias.IsChecked = false;
        await vm.ExtractCommand.ExecuteAsync(null);

        vm.HasResult.ShouldBeTrue();
        vm.BiasSummaryText.ShouldBeEmpty("no bias assigned — no bias summary on the result");
    }

    [Fact]
    public async Task Extract_PinCheckedAsBias_RevokesItsInputTwin()
    {
        var (vm, _) = ConfigureForGroup(LogicGateFixtureFactory.CreateCombinerGroup());
        CheckPins(vm, new[] { "a" }, new[] { "y" });
        CheckBiasPins(vm, "a");

        await vm.ExtractCommand.ExecuteAsync(null);

        vm.InputPins.Single(p => p.PinName == "a").IsChecked.ShouldBeFalse(
            "checking 'a' as bias revokes its input role — a pin has exactly one role");
        vm.StatusText.ShouldBe(Translate("Analysis.TruthTable.SelectPins"),
            "with the sole input revoked, no input bit remains — the panel asks for one");
        vm.HasResult.ShouldBeFalse();
        vm.IsProcessing.ShouldBeFalse();
    }

    [Fact]
    public async Task Extract_NoPinsSelected_ShowsMessage()
    {
        var (vm, _) = ConfigureForGroup(LogicGateFixtureFactory.CreateCombinerGroup());

        await vm.ExtractCommand.ExecuteAsync(null);

        vm.StatusText.ShouldBe(Translate("Analysis.TruthTable.SelectPins"));
        vm.HasResult.ShouldBeFalse();
        vm.IsProcessing.ShouldBeFalse();
    }

    [Fact]
    public async Task Extract_UnknownPin_ShowsMessageInsteadOfThrowing()
    {
        var group = LogicGateFixtureFactory.CreateCombinerGroup();
        var (vm, _) = ConfigureForGroup(group);
        CheckPins(vm, new[] { "a", "b" }, new[] { "y" });
        // The pin list was built from the group's pins; renaming one afterwards makes
        // the extractor reject the stale name — the panel must show it, not crash.
        group.ExternalPins.First(p => p.Name == "y").Name = "z";

        await vm.ExtractCommand.ExecuteAsync(null);

        vm.StatusText.ShouldContain("'y'");
        vm.HasResult.ShouldBeFalse();
        vm.IsProcessing.ShouldBeFalse();
    }

    [Fact]
    public async Task Extract_PinCheckedAsOutput_RevokesItsInputTwin()
    {
        var (vm, _) = ConfigureForGroup(LogicGateFixtureFactory.CreateCombinerGroup());
        CheckPins(vm, new[] { "a" }, new[] { "a" });

        await vm.ExtractCommand.ExecuteAsync(null);

        vm.InputPins.Single(p => p.PinName == "a").IsChecked.ShouldBeFalse(
            "checking 'a' as output revokes its input role — a pin has exactly one role");
        vm.StatusText.ShouldBe(Translate("Analysis.TruthTable.SelectPins"),
            "with the input role revoked, no input remains — the panel asks for one");
        vm.HasResult.ShouldBeFalse();
        vm.IsProcessing.ShouldBeFalse();
    }

    [Fact]
    public async Task Extract_ThresholdOutsideOpenInterval_ShowsMessage()
    {
        var (vm, _) = ConfigureForGroup(LogicGateFixtureFactory.CreateCombinerGroup());
        CheckPins(vm, new[] { "a", "b" }, new[] { "y" });
        vm.Threshold = 1.0;

        await vm.ExtractCommand.ExecuteAsync(null);

        vm.StatusText.ShouldContain("threshold");
        vm.HasResult.ShouldBeFalse();
        vm.IsProcessing.ShouldBeFalse();
    }

    [Fact]
    public async Task Extract_Cancelled_StopsWithCancelledMessage()
    {
        var (vm, _) = ConfigureForGroup(LogicGateFixtureFactory.CreateCombinerGroup());
        CheckPins(vm, new[] { "a", "b" }, new[] { "y" });
        // IsProcessing flips synchronously before the extractor starts, so this cancel
        // lands before the first combination — deterministic, no timing race.
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TruthTableViewModel.IsProcessing) && vm.IsProcessing)
                vm.CancelCommand.Execute(null);
        };

        await vm.ExtractCommand.ExecuteAsync(null);

        vm.StatusText.ShouldBe(Translate("Analysis.TruthTable.Cancelled"));
        vm.IsProcessing.ShouldBeFalse();
        vm.HasResult.ShouldBeFalse();
    }

    [Fact]
    public async Task ConfigureForSelection_WhileExtracting_CancelsRunAndShowsNoStaleResult()
    {
        var (vm, _) = ConfigureForGroup(LogicGateFixtureFactory.CreateCombinerGroup());
        CheckPins(vm, new[] { "a", "b" }, new[] { "y" });
        // IsProcessing flips synchronously before the extractor starts, so the
        // re-selection lands while the run is in flight — deterministic, no timing race.
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TruthTableViewModel.IsProcessing) && vm.IsProcessing)
                vm.ConfigureForSelection(null, new DesignCanvasViewModel());
        };

        await vm.ExtractCommand.ExecuteAsync(null);

        vm.IsGroupSelected.ShouldBeFalse();
        vm.HasResult.ShouldBeFalse();
        vm.IsProcessing.ShouldBeFalse();
        vm.Rows.ShouldBeEmpty();
    }

    [Fact]
    public void PinRoles_CheckingOneRole_UnchecksTheSamePinInTheOtherTwoLists()
    {
        var (vm, _) = ConfigureForGroup(LogicGateFixtureFactory.CreateCombinerGroup());
        CheckPins(vm, new[] { "a", "b" }, new[] { "y" });
        CheckBiasPins(vm, "a");

        vm.InputPins.Single(p => p.PinName == "a").IsChecked.ShouldBeFalse(
            "checking 'a' as bias revokes its input role — a pin has exactly one role");
        vm.InputPins.Single(p => p.PinName == "b").IsChecked.ShouldBeTrue(
            "an unrelated pin keeps its role");

        vm.BiasPins.Single(p => p.PinName == "b").IsChecked = true;
        vm.InputPins.Single(p => p.PinName == "b").IsChecked.ShouldBeFalse(
            "the bias check takes over from both roles at once");

        vm.OutputPins.Single(p => p.PinName == "a").IsChecked = true;
        vm.BiasPins.Single(p => p.PinName == "a").IsChecked.ShouldBeFalse(
            "re-checking as output revokes the bias role");
        vm.OutputPins.Single(p => p.PinName == "a").IsChecked.ShouldBeTrue();

        vm.InputPins.Single(p => p.PinName == "a").IsChecked = true;
        vm.OutputPins.Single(p => p.PinName == "a").IsChecked.ShouldBeFalse(
            "re-checking as input revokes the output role");
        vm.InputPins.Single(p => p.PinName == "a").IsChecked.ShouldBeTrue();
    }
}
