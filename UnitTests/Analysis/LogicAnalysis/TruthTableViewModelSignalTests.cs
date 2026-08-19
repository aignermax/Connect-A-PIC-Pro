using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Analysis.LogicAnalysis;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.LogicAnalysis;

/// <summary>
/// Signal-name editing in the Truth Table panel (issue #1033): once a group carries
/// a persisted pin assignment (after extraction or load), each input row offers an
/// editable signal field whose edits write
/// <see cref="TruthTablePinAssignment.InputSignalNames"/> on the group — trimmed,
/// empty-after-trim meaning "no signal", an emptied map collapsing to null so legacy
/// files stay byte-clean. A pin losing its input role drops its signal identity, and
/// two pins of different gates named with one signal merge into a single network
/// input at build time.
/// </summary>
public class TruthTableViewModelSignalTests
{
    private const double CombinerThreshold = 0.25;

    [Fact]
    public async Task SignalEdit_CheckedInput_WritesTrimmedNameIntoPersistedAssignment()
    {
        var (vm, group) = await ConfigureExtractedCombiner();

        vm.InputPins.Single(p => p.PinName == "a").SignalName = "  Sum  ";

        group.TruthTablePinAssignment!.InputSignalNames.ShouldBe(
            new Dictionary<string, string> { ["a"] = "Sum" },
            "the edit writes through to the persisted map, trimmed");
    }

    [Fact]
    public async Task SignalEdit_EmptyAfterTrim_RemovesEntry_AndEmptiedMapCollapsesToNull()
    {
        var (vm, group) = await ConfigureExtractedCombiner();
        var pin = vm.InputPins.Single(p => p.PinName == "a");

        pin.SignalName = "Sum";
        pin.SignalName = "   ";

        group.TruthTablePinAssignment!.InputSignalNames.ShouldBeNull(
            "clearing the only named pin empties the map — it persists as null, byte-clean");
    }

    [Fact]
    public async Task SignalEdit_ClearingOneOfTwo_KeepsTheOtherEntry()
    {
        var (vm, group) = await ConfigureExtractedCombiner();
        vm.InputPins.Single(p => p.PinName == "a").SignalName = "Sum";
        vm.InputPins.Single(p => p.PinName == "b").SignalName = "Carry";

        vm.InputPins.Single(p => p.PinName == "a").SignalName = "";

        group.TruthTablePinAssignment!.InputSignalNames.ShouldBe(
            new Dictionary<string, string> { ["b"] = "Carry" });
    }

    [Fact]
    public void SignalEdit_BeforeExtraction_PersistsNothing()
    {
        var group = LogicGateFixtureFactory.CreateCombinerGroup();
        var vm = Configure(group);
        vm.SignalNamesVisible.ShouldBeFalse("no extraction yet — the signal column stays hidden");
        vm.InputPins.Single(p => p.PinName == "a").IsChecked = true;

        vm.InputPins.Single(p => p.PinName == "a").SignalName = "Sum";

        group.TruthTablePinAssignment.ShouldBeNull(
            "without a persisted assignment there is nothing to attach a signal name to");
    }

    [Fact]
    public async Task InputPin_Unchecked_RevokesItsSignalIdentity()
    {
        var (vm, group) = await ConfigureExtractedCombiner();
        var pin = vm.InputPins.Single(p => p.PinName == "a");
        pin.SignalName = "Sum";

        pin.IsChecked = false;

        pin.SignalName.ShouldBeEmpty("the field mirrors the map — a non-input pin carries no signal");
        group.TruthTablePinAssignment!.InputSignalNames.ShouldBeNull();
    }

    [Fact]
    public async Task PinCheckedAsOutput_RevokesItsInputSignalIdentity()
    {
        var (vm, group) = await ConfigureExtractedCombiner();
        vm.InputPins.Single(p => p.PinName == "a").SignalName = "Sum";

        vm.OutputPins.Single(p => p.PinName == "a").IsChecked = true;

        group.TruthTablePinAssignment!.InputSignalNames.ShouldBeNull(
            "the pin changed roles — its signal identity is gone");
        vm.InputPins.Single(p => p.PinName == "a").SignalName.ShouldBeEmpty();
    }

    [Fact]
    public async Task ConfigureForSelection_PersistedSignalNames_PrefillInputRowsAndShowColumn()
    {
        var (vm, group) = await ConfigureExtractedCombiner();
        vm.InputPins.Single(p => p.PinName == "a").SignalName = "Sum";

        var reopened = Configure(group);

        reopened.SignalNamesVisible.ShouldBeTrue("the group carries a persisted assignment");
        reopened.InputPins.Single(p => p.PinName == "a").SignalName.ShouldBe("Sum",
            "the persisted signal name prefills the input row");
        reopened.InputPins.Single(p => p.PinName == "b").SignalName.ShouldBeEmpty();
    }

    [Fact]
    public void SameSignalName_OnTwoGateInputs_MergesIntoOneNetworkInputDrivingBoth()
    {
        // Two gates, each extracted as NOT with its A pin still unnamed — the panel
        // rename below is the only source of signal identity.
        var first = CreateNotGroupWithPersistedRoles("INV1");
        var second = CreateNotGroupWithPersistedRoles("INV2");
        RenameSignalThroughPanel(first, "S");
        RenameSignalThroughPanel(second, "S");

        var network = new LogicNetworkBuilder().Build(
            new[] { GateInstance(first), GateInstance(second) },
            Array.Empty<WaveguideConnection>());

        network.InputPinNames.ShouldBe(new[] { "S" },
            "both gates' A pins carry signal S — one Logic panel toggle, not two");
        var fanOut = network.FanOutWarnings.Single(w => w.DriverDisplayName == "S");
        fanOut.LoadNames.ShouldBe(new[] { "INV1.A", "INV2.A" }, ignoreOrder: true,
            "both pins are members of the one network input");
        foreach (var bit in new[] { false, true })
        {
            var outputs = network.Evaluate(new Dictionary<string, bool> { ["S"] = bit });
            outputs["INV1.Y"].ShouldBe(!bit);
            outputs["INV2.Y"].ShouldBe(!bit, "the one signal bit drives every member pin");
        }
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

    /// <summary>Renames the group's A-pin signal through the panel, as the user would.</summary>
    private static void RenameSignalThroughPanel(ComponentGroup group, string signal)
    {
        var vm = Configure(group);
        vm.SignalNamesVisible.ShouldBeTrue("a persisted assignment makes the signal column editable");
        vm.InputPins.Single(p => p.PinName == "A").SignalName = signal;
        group.TruthTablePinAssignment!.InputSignalNames.ShouldNotBeNull()
            ["A"].ShouldBe(signal, "the panel edit writes into the persisted assignment");
    }

    /// <summary>The gate instance the assembler would build from the group's persisted roles.</summary>
    private static LogicGateInstance GateInstance(ComponentGroup group)
    {
        var roles = group.TruthTablePinAssignment!;
        return new LogicGateInstance(
            group,
            PinnedGateTables.NotGate(),
            new GateRoleAssignment(
                roles.InputPinNames, roles.OutputPinNames, roles.BiasPinNames, roles.Threshold,
                roles.InputSignalNames));
    }

    /// <summary>A bare NOT-shaped group with persisted roles but no signal names yet.</summary>
    private static ComponentGroup CreateNotGroupWithPersistedRoles(string groupName)
    {
        var group = new ComponentGroup(groupName);
        foreach (var pinName in new[] { "A", "BIAS", "Y" })
        {
            var physicalPin = new PhysicalPin { Name = pinName, ParentComponent = group };
            group.PhysicalPins.Add(physicalPin);
            group.AddExternalPin(new GroupPin { Name = pinName, InternalPin = physicalPin });
        }
        group.TruthTablePinAssignment = new TruthTablePinAssignment
        {
            InputPinNames = new List<string> { "A" },
            OutputPinNames = new List<string> { "Y" },
            BiasPinNames = new List<string> { "BIAS" },
            Threshold = PinnedGateTables.NotThreshold,
        };
        return group;
    }
}
