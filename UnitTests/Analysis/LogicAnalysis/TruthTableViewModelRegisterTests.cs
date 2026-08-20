using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Analysis.LogicAnalysis;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.LogicAnalysis;

/// <summary>
/// The "Register (state element)" toggle in the Truth Table panel (issue #1098, UI
/// slice of #1086): it binds the persisted <see cref="TruthTablePinAssignment.IsRegister"/>
/// flag of the selected group — prefilled from the assignment on selection, written
/// through on toggle, and carried into the assignment the extraction creates. A
/// network built from a group toggled as register accepts a feedback loop;
/// untoggling restores the honest combinational-cycle rejection.
/// </summary>
public class TruthTableViewModelRegisterTests
{
    private const double OrThreshold = 0.25;

    [Fact]
    public async Task Toggle_AfterExtraction_WritesThroughToThePersistedAssignment()
    {
        var (vm, group) = await ConfigureExtractedCombiner();

        vm.IsRegister.ShouldBeFalse("a fresh extraction designates no register");
        group.TruthTablePinAssignment!.IsRegister.ShouldBeFalse();

        vm.IsRegister = true;
        group.TruthTablePinAssignment!.IsRegister.ShouldBeTrue(
            "the toggle writes through to the persisted assignment");

        vm.IsRegister = false;
        group.TruthTablePinAssignment!.IsRegister.ShouldBeFalse(
            "untoggling clears the designation");
    }

    [Fact]
    public void ConfigureForSelection_PersistedRegisterDesignation_PrefillsTheToggle()
    {
        var group = LogicGateFixtureFactory.CreateCombinerGroup();
        group.TruthTablePinAssignment = PersistedOrRoles(isRegister: true);

        var vm = Configure(group);

        vm.IsRegister.ShouldBeTrue("the persisted designation prefills the toggle");
        vm.IsRegister = false;
        group.TruthTablePinAssignment.IsRegister.ShouldBeFalse(
            "a user edit after the prefill writes back into the persisted assignment");
    }

    [Fact]
    public void ConfigureForSelection_SwitchingToAPlainGroup_ResetsTheToggle()
    {
        var registerGroup = LogicGateFixtureFactory.CreateCombinerGroup();
        registerGroup.TruthTablePinAssignment = PersistedOrRoles(isRegister: true);
        var plainGroup = LogicGateFixtureFactory.CreateCombinerGroup();
        var canvas = new DesignCanvasViewModel();
        var vm = new TruthTableViewModel();

        vm.ConfigureForSelection(new ComponentViewModel(registerGroup), canvas);
        vm.IsRegister.ShouldBeTrue();

        vm.ConfigureForSelection(new ComponentViewModel(plainGroup), canvas);
        vm.IsRegister.ShouldBeFalse(
            "a group without designation must not inherit the previous group's toggle");
    }

    [Fact]
    public async Task Toggle_BeforeFirstExtraction_PersistsWithTheExtractedAssignment()
    {
        var group = LogicGateFixtureFactory.CreateCombinerGroup();
        var vm = Configure(group);
        vm.IsRegister = true;
        group.TruthTablePinAssignment.ShouldBeNull(
            "before the first extraction there is nothing to attach the flag to");

        CheckOrRoles(vm);
        await vm.ExtractCommand.ExecuteAsync(null);

        vm.HasResult.ShouldBeTrue("the extraction must succeed");
        group.TruthTablePinAssignment!.IsRegister.ShouldBeTrue(
            "the toggle intent rides into the assignment the extraction persists");
    }

    [Fact]
    public async Task ReExtraction_PreservesTheRegisterDesignation()
    {
        var (vm, group) = await ConfigureExtractedCombiner();
        vm.IsRegister = true;

        await vm.ExtractCommand.ExecuteAsync(null);

        vm.HasResult.ShouldBeTrue("the re-extraction must succeed");
        group.TruthTablePinAssignment!.IsRegister.ShouldBeTrue(
            "re-extraction must not strip the designation");
        vm.IsRegister.ShouldBeTrue("the toggle keeps mirroring the persisted flag");
    }

    [Fact]
    public async Task FeedbackLoop_RegisterToggledThroughPanel_Assembles_UntoggleRestoresCycleRejection()
    {
        var first = CombinerAsOrGate("OR1");
        var second = CombinerAsOrGate("OR2");
        var connections = FeedbackLoop(first, second);

        var rejected = await Should.ThrowAsync<InvalidOperationException>(
            () => Assemble(first, second, connections));
        // The honestly combinational loop keeps its cycle rejection.
        rejected.Message.ShouldContain("sequential logic is not supported");

        ToggleRegisterThroughPanel(second, value: true);
        var network = await Assemble(first, second, connections);
        network.RegisterState.Keys.ShouldBe(new[] { new LogicPinRef("OR2", "y") },
            "the panel toggled the designation the assembler builds the register from");

        ToggleRegisterThroughPanel(second, value: false);
        var rejectedAgain = await Should.ThrowAsync<InvalidOperationException>(
            () => Assemble(first, second, connections));
        // Untoggling restores the honest combinational-cycle rejection.
        rejectedAgain.Message.ShouldContain("cycle");
    }

    /// <summary>Toggles the panel's register checkbox over the group, as the user would.</summary>
    private static void ToggleRegisterThroughPanel(ComponentGroup group, bool value)
    {
        var vm = Configure(group);
        vm.IsRegister.ShouldBe(!value, "the toggle starts at the persisted designation");
        vm.IsRegister = value;
        group.TruthTablePinAssignment!.IsRegister.ShouldBe(value,
            "the toggle writes through to the persisted assignment");
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

    /// <summary>Runs the combiner fixture through a real extraction, seeding the persisted assignment.</summary>
    private static async Task<(TruthTableViewModel Vm, ComponentGroup Group)> ConfigureExtractedCombiner()
    {
        var group = LogicGateFixtureFactory.CreateCombinerGroup();
        var vm = Configure(group);
        CheckOrRoles(vm);
        await vm.ExtractCommand.ExecuteAsync(null);
        vm.HasResult.ShouldBeTrue("the extraction seeds the persisted assignment");
        return (vm, group);
    }

    /// <summary>Ticks the combiner's OR reading: a and b as inputs, y as output.</summary>
    private static void CheckOrRoles(TruthTableViewModel vm)
    {
        vm.InputPins.Single(p => p.PinName == "a").IsChecked = true;
        vm.InputPins.Single(p => p.PinName == "b").IsChecked = true;
        vm.OutputPins.Single(p => p.PinName == "y").IsChecked = true;
        vm.Threshold = OrThreshold;
    }

    /// <summary>The combiner's OR-reading assignment in its persisted form.</summary>
    private static TruthTablePinAssignment PersistedOrRoles(bool isRegister) => new()
    {
        InputPinNames = new List<string> { "a", "b" },
        OutputPinNames = new List<string> { "y" },
        BiasPinNames = new List<string>(),
        Threshold = OrThreshold,
        IsRegister = isRegister,
    };

    /// <summary>A combiner group carrying the persisted OR reading, ready for assembly.</summary>
    private static ComponentGroup CombinerAsOrGate(string groupName)
    {
        var group = LogicGateFixtureFactory.CreateCombinerGroup();
        group.GroupName = groupName;
        group.TruthTablePinAssignment = PersistedOrRoles(isRegister: false);
        group.EnsureSMatrixComputed();
        return group;
    }

    /// <summary>The cross-wired feedback loop: OR1.y → OR2.a and OR2.y → OR1.a.</summary>
    private static WaveguideConnection[] FeedbackLoop(ComponentGroup first, ComponentGroup second) =>
        new[] { Connect(first, "y", second, "a"), Connect(second, "y", first, "a") };

    /// <summary>Runs the assembler at the fixture wavelength.</summary>
    private static Task<LogicNetworkEvaluator> Assemble(
        ComponentGroup first, ComponentGroup second, IReadOnlyList<WaveguideConnection> connections) =>
        new LogicNetworkAssembler().AssembleAsync(
            new Component[] { first, second }, connections, LogicGateFixtureFactory.WavelengthNm);

    /// <summary>A design connection between two gate groups' external pins.</summary>
    private static WaveguideConnection Connect(
        ComponentGroup from, string fromPin, ComponentGroup to, string toPin) =>
        new() { StartPin = Pin(from, fromPin), EndPin = Pin(to, toPin) };

    /// <summary>Looks up a group's connectable external pin.</summary>
    private static PhysicalPin Pin(ComponentGroup group, string name) =>
        group.PhysicalPins.Single(p => p.Name == name);
}
