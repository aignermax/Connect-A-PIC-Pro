using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Analysis.LogicAnalysis;
using CAP_Core.Components.Core;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.LogicAnalysis;

/// <summary>
/// Output signal-name editing in the Truth Table panel (issue #1046): once a group
/// carries a persisted pin assignment (after extraction or load), each checked
/// output row offers the same editable signal field the input rows got in #1033,
/// writing <see cref="TruthTablePinAssignment.OutputSignalNames"/> on the group —
/// trimmed, empty-after-trim meaning "no signal", an emptied map collapsing to null
/// so legacy files stay byte-clean. A pin losing its output role drops its signal
/// identity, and a named output's network tap reads the signal name (S, Cout)
/// instead of the raw <c>&lt;gate&gt;.&lt;pin&gt;</c> id.
/// </summary>
public class TruthTableViewModelOutputSignalTests
{
    private const double CombinerThreshold = 0.25;

    [Fact]
    public async Task OutputSignalEdit_CheckedOutput_WritesTrimmedNameIntoPersistedAssignment()
    {
        var (vm, group) = await ConfigureExtractedCombiner();

        vm.OutputPins.Single(p => p.PinName == "y").SignalName = "  Sum  ";

        group.TruthTablePinAssignment!.OutputSignalNames.ShouldBe(
            new Dictionary<string, string> { ["y"] = "Sum" },
            "the edit writes through to the persisted map, trimmed");
    }

    [Fact]
    public async Task OutputSignalEdit_EmptyAfterTrim_RemovesEntry_AndEmptiedMapCollapsesToNull()
    {
        var (vm, group) = await ConfigureExtractedCombiner();
        var pin = vm.OutputPins.Single(p => p.PinName == "y");

        pin.SignalName = "Sum";
        pin.SignalName = "   ";

        group.TruthTablePinAssignment!.OutputSignalNames.ShouldBeNull(
            "clearing the only named output empties the map — it persists as null, byte-clean");
    }

    [Fact]
    public void OutputSignalEdit_ClearingOneOfTwo_KeepsTheOtherEntry()
    {
        var group = CreateTwoOutputGroupWithPersistedRoles();
        var vm = Configure(group);
        vm.OutputPins.Single(p => p.PinName == "S").SignalName = "Sum";
        vm.OutputPins.Single(p => p.PinName == "C").SignalName = "Carry";

        vm.OutputPins.Single(p => p.PinName == "S").SignalName = "";

        group.TruthTablePinAssignment!.OutputSignalNames.ShouldBe(
            new Dictionary<string, string> { ["C"] = "Carry" });
    }

    [Fact]
    public void OutputSignalEdit_BeforeExtraction_PersistsNothing()
    {
        var group = LogicGateFixtureFactory.CreateCombinerGroup();
        var vm = Configure(group);
        vm.SignalNamesVisible.ShouldBeFalse("no extraction yet — the signal column stays hidden");
        vm.OutputPins.Single(p => p.PinName == "y").IsChecked = true;

        vm.OutputPins.Single(p => p.PinName == "y").SignalName = "Sum";

        group.TruthTablePinAssignment.ShouldBeNull(
            "without a persisted assignment there is nothing to attach a signal name to");
    }

    [Fact]
    public async Task OutputPin_Unchecked_RevokesItsSignalIdentity()
    {
        var (vm, group) = await ConfigureExtractedCombiner();
        var pin = vm.OutputPins.Single(p => p.PinName == "y");
        pin.SignalName = "Sum";

        pin.IsChecked = false;

        pin.SignalName.ShouldBeEmpty("the field mirrors the map — a non-output pin carries no signal");
        group.TruthTablePinAssignment!.OutputSignalNames.ShouldBeNull();
    }

    [Fact]
    public async Task PinCheckedAsInput_RevokesItsOutputSignalIdentity()
    {
        var (vm, group) = await ConfigureExtractedCombiner();
        vm.OutputPins.Single(p => p.PinName == "y").SignalName = "Sum";

        vm.InputPins.Single(p => p.PinName == "y").IsChecked = true;

        group.TruthTablePinAssignment!.OutputSignalNames.ShouldBeNull(
            "the pin changed roles — its output signal identity is gone");
        vm.OutputPins.Single(p => p.PinName == "y").SignalName.ShouldBeEmpty();
    }

    [Fact]
    public async Task PinCheckedAsBias_RevokesItsOutputSignalIdentity()
    {
        var (vm, group) = await ConfigureExtractedCombiner();
        vm.OutputPins.Single(p => p.PinName == "y").SignalName = "Sum";

        vm.BiasPins.Single(p => p.PinName == "y").IsChecked = true;

        group.TruthTablePinAssignment!.OutputSignalNames.ShouldBeNull(
            "a bias pin takes no part in logic — the output signal identity is gone");
        vm.OutputPins.Single(p => p.PinName == "y").SignalName.ShouldBeEmpty();
    }

    [Fact]
    public async Task ConfigureForSelection_PersistedOutputSignalNames_PrefillOutputRows()
    {
        var (vm, group) = await ConfigureExtractedCombiner();
        vm.OutputPins.Single(p => p.PinName == "y").SignalName = "Sum";

        var reopened = Configure(group);

        reopened.SignalNamesVisible.ShouldBeTrue("the group carries a persisted assignment");
        reopened.OutputPins.Single(p => p.PinName == "y").SignalName.ShouldBe("Sum",
            "the persisted signal name prefills the output row");
    }

    [Fact]
    public async Task InputAndOutputSignalNames_StayInTheirOwnMaps()
    {
        var (vm, group) = await ConfigureExtractedCombiner();

        vm.InputPins.Single(p => p.PinName == "a").SignalName = "OperandA";
        vm.OutputPins.Single(p => p.PinName == "y").SignalName = "Sum";

        var assignment = group.TruthTablePinAssignment!;
        assignment.InputSignalNames.ShouldBe(new Dictionary<string, string> { ["a"] = "OperandA" });
        assignment.OutputSignalNames.ShouldBe(new Dictionary<string, string> { ["y"] = "Sum" },
            "input and output names never share a map");
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
        vm.InputPins.Single(p => p.PinName == "a").IsChecked = true;
        vm.InputPins.Single(p => p.PinName == "b").IsChecked = true;
        vm.OutputPins.Single(p => p.PinName == "y").IsChecked = true;
        vm.Threshold = CombinerThreshold;
        await vm.ExtractCommand.ExecuteAsync(null);
        vm.HasResult.ShouldBeTrue("the extraction seeds the persisted assignment");
        vm.SignalNamesVisible.ShouldBeTrue("after extraction the signal column shows");
        return (vm, group);
    }

    /// <summary>A bare group with two persisted logic outputs but no signal names yet.</summary>
    private static ComponentGroup CreateTwoOutputGroupWithPersistedRoles()
    {
        var group = new ComponentGroup("ADDER");
        foreach (var pinName in new[] { "A", "BIAS", "S", "C" })
        {
            var physicalPin = new PhysicalPin { Name = pinName, ParentComponent = group };
            group.PhysicalPins.Add(physicalPin);
            group.AddExternalPin(new GroupPin { Name = pinName, InternalPin = physicalPin });
        }
        group.TruthTablePinAssignment = new TruthTablePinAssignment
        {
            InputPinNames = new List<string> { "A" },
            OutputPinNames = new List<string> { "S", "C" },
            BiasPinNames = new List<string> { "BIAS" },
            Threshold = PinnedGateTables.NotThreshold,
        };
        return group;
    }
}
