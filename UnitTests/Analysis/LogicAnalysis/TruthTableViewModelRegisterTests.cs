using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Analysis.LogicAnalysis;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.LogicAnalysis;

/// <summary>
/// The register toggle in the Truth Table panel (issue #1098): it designates the
/// selected gate group as a behavioral state element by setting
/// <see cref="TruthTablePinAssignment.IsRegister"/> on the group's persisted
/// assignment — write-through once an assignment exists, folded into the assignment
/// the next extraction persists when none exists yet. A re-extraction keeps the
/// designation (#1093 pinned at the panel level), a reopened panel prefills the
/// toggle, and only a designated group turns the honest combinational-cycle
/// rejection into an accepted feedback loop at assembly time.
/// </summary>
public class TruthTableViewModelRegisterTests
{
    private const double CombinerThreshold = 0.25;

    [Fact]
    public async Task ToggleAfterExtraction_WritesThroughToPersistedAssignment()
    {
        var (vm, group) = await ConfigureExtractedCombiner();
        vm.IsRegister.ShouldBeFalse("a freshly extracted gate is plain combinational");

        vm.IsRegister = true;

        group.TruthTablePinAssignment!.IsRegister.ShouldBeTrue(
            "the toggle writes straight into the persisted assignment");

        vm.IsRegister = false;

        group.TruthTablePinAssignment!.IsRegister.ShouldBeFalse(
            "untoggling clears the designation again");
    }

    [Fact]
    public async Task ToggleBeforeExtraction_RidesIntoThePersistedAssignment()
    {
        var group = LogicGateFixtureFactory.CreateCombinerGroup();
        var vm = Configure(group);
        group.TruthTablePinAssignment.ShouldBeNull("nothing persisted before the first extraction");

        vm.IsRegister = true;

        group.TruthTablePinAssignment.ShouldBeNull(
            "with no assignment present the toggle stays a pending designation");

        await Extract(vm);

        group.TruthTablePinAssignment!.IsRegister.ShouldBeTrue(
            "the extraction folds the pending designation into the persisted assignment");
    }

    [Fact]
    public async Task ReExtraction_PreservesTheRegisterDesignation()
    {
        var (vm, group) = await ConfigureExtractedCombiner();
        vm.IsRegister = true;

        await Extract(vm);

        group.TruthTablePinAssignment!.IsRegister.ShouldBeTrue(
            "re-extracting the same gate must not silently drop its state-element designation");
        vm.IsRegister.ShouldBeTrue();
    }

    [Fact]
    public async Task ConfigureForSelection_PersistedDesignation_PrefillsToggle()
    {
        var (vm, group) = await ConfigureExtractedCombiner();
        vm.IsRegister = true;

        var reopened = Configure(group);

        reopened.IsRegister.ShouldBeTrue(
            "the reopened panel shows the designation the group was saved with");
    }

    [Fact]
    public async Task ConfigureForSelection_GroupWithoutAssignment_ResetsToggle()
    {
        var (vm, _) = await ConfigureExtractedCombiner();
        vm.IsRegister = true;

        // Re-selecting a different group on the same panel must not leak the flag over.
        var plainCanvas = new DesignCanvasViewModel();
        var plainComponent = new ComponentViewModel(LogicGateFixtureFactory.CreateCombinerGroup());
        plainCanvas.Selection.SelectSingle(plainComponent);
        vm.ConfigureForSelection(plainComponent, plainCanvas);

        vm.IsRegister.ShouldBeFalse(
            "a group that never carried an assignment starts unchecked — the previous group's flag must not leak over");
    }

    [Fact]
    public async Task FeedbackLoop_AssemblesWhenToggledAsRegister_AndKeepsCycleRejectionWhenUntoggled()
    {
        var first = OrGate("OR1");
        var second = OrGate("OR2");
        var vm = Configure(second);

        vm.IsRegister = true;

        var network = await Assemble(first, second);
        network.RegisterState.Keys.ShouldBe(new[] { new LogicPinRef("OR2", "y") },
            "the toggle designated the second gate as the loop's register");

        vm.IsRegister = false;

        var error = await Should.ThrowAsync<InvalidOperationException>(() => Assemble(first, second));
        error.Message.ShouldContain("cycle");
        error.Message.ShouldContain("sequential logic is not supported");
    }

    /// <summary>Configures the panel for the group and returns the ViewModel.</summary>
    private static TruthTableViewModel Configure(ComponentGroup group)
    {
        var canvas = new DesignCanvasViewModel();
        var component = new ComponentViewModel(group);
        canvas.Selection.SelectSingle(component);
        var vm = new TruthTableViewModel();
        vm.ConfigureForSelection(component, canvas);
        return vm;
    }

    /// <summary>Extracts the combiner fixture as an OR gate through the panel.</summary>
    private static async Task Extract(TruthTableViewModel vm)
    {
        vm.InputPins.Single(p => p.PinName == "a").IsChecked = true;
        vm.InputPins.Single(p => p.PinName == "b").IsChecked = true;
        vm.OutputPins.Single(p => p.PinName == "y").IsChecked = true;
        vm.Threshold = CombinerThreshold;
        await vm.ExtractCommand.ExecuteAsync(null);
        vm.HasResult.ShouldBeTrue("the extraction seeds the persisted assignment");
    }

    private static async Task<(TruthTableViewModel Vm, ComponentGroup Group)> ConfigureExtractedCombiner()
    {
        var group = LogicGateFixtureFactory.CreateCombinerGroup();
        var vm = Configure(group);
        await Extract(vm);
        return (vm, group);
    }

    /// <summary>A combiner group carrying the persisted OR-reading roles, without the register flag.</summary>
    private static ComponentGroup OrGate(string groupName)
    {
        var group = LogicGateFixtureFactory.CreateCombinerGroup();
        group.GroupName = groupName;
        group.TruthTablePinAssignment = new TruthTablePinAssignment
        {
            InputPinNames = new List<string> { "a", "b" },
            OutputPinNames = new List<string> { "y" },
            BiasPinNames = new List<string>(),
            Threshold = CombinerThreshold,
        };
        group.EnsureSMatrixComputed();
        return group;
    }

    /// <summary>Assembles the cross-wired feedback loop: first.y → second.a and second.y → first.a.</summary>
    private static Task<LogicNetworkEvaluator> Assemble(ComponentGroup first, ComponentGroup second) =>
        new LogicNetworkAssembler().AssembleAsync(
            new Component[] { first, second },
            new[] { Connect(first, second), Connect(second, first) },
            LogicGateFixtureFactory.WavelengthNm);

    /// <summary>Feeds the source group's output y into the target group's input a.</summary>
    private static WaveguideConnection Connect(ComponentGroup from, ComponentGroup to) => new()
    {
        StartPin = from.PhysicalPins.Single(p => p.Name == "y"),
        EndPin = to.PhysicalPins.Single(p => p.Name == "a"),
    };
}
