using System.Collections.ObjectModel;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Export;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Analysis.LogicAnalysis;
using CAP_Core.Components.Core;
using CAP_Core.Export;
using Shouldly;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Headless load test for the shipped logic-gate example
/// <c>examples/Logic Gate NOT-NAND.lun</c> (issue #964, rung 4 of the NAND game):
/// the file loads through the real load path as a single "NOT/NAND Gate" group
/// built only from bundled Demo PDK components. The BIAS pin — 'on' in every row and
/// shifted 135° against the combined A·B beam — cancels it in the recombination
/// coupler exactly when both inputs are on (raw power 0). Enumerate only A at
/// threshold 0.375 for NOT (resting 0.5 bright, single-input 0.25 dark), enumerate
/// A and B at threshold 0.125 for NAND: raw powers 0.5 / 0.25 / 0.25 / 0.0.
/// </summary>
public class LogicGateNotNandExampleTests
{
    private const string ExampleFileName = "Logic Gate NOT-NAND.lun";
    private const string GroupName = "NOT/NAND Gate";
    private const double PowerTolerance = 1e-6;
    private const double NotThreshold = 0.375;
    private const double NandThreshold = 0.125;
    private const double RestingPower = 0.5;
    private const double SingleInputPower = 0.25;
    private const int WavelengthNm = 1550;

    private static readonly string[] InputA = { "A" };
    private static readonly string[] InputsAb = { "A", "B" };
    private static readonly string[] Biases = { "BIAS" };
    private static readonly string[] Outputs = { "Y" };

    [Fact]
    public async Task Example_LoadsAsSingleGroup_WithCleanExternalPins()
    {
        var group = await LoadGateGroup();

        group.GroupName.ShouldBe(GroupName);
        // The group description carries both thresholds plus the phase lesson.
        group.Description.ShouldContain("0.375");
        group.Description.ShouldContain("0.125");
        group.Description.ShouldContain("135");
        group.ChildComponents.Select(c => c.Identifier)
            .ShouldBe(new[] { "input_a", "input_b", "combine", "phase", "recombine", "output" }, ignoreOrder: true);
        group.InternalPaths.Count.ShouldBe(5,
            "both inputs, the stage link, the phase result, and the output are routed inside the group");
        group.ExternalPins.Select(p => p.Name)
            .ShouldBe(new[] { "A", "B", "BIAS", "Y" }, ignoreOrder: true);
        group.ExternalPins.ShouldAllBe(p => p.InternalPin != null && p.InternalPin.LogicalPin != null,
            "every external pin stays bound to a simulatable component pin");
    }

    [Fact]
    public async Task TruthTable_InputAOnlyAtThreshold0375_ProducesNotBitsWithRawPowers()
    {
        var table = await Extract(InputA, NotThreshold);

        table.BiasPinNames.ShouldBe(Biases);
        table.Rows.Count.ShouldBe(2, "one logic input produces two rows");
        AssertRow(table, 0, expectedBit: true, expectedPower: RestingPower);
        AssertRow(table, 1, expectedBit: false, expectedPower: SingleInputPower);
    }

    [Fact]
    public async Task TruthTable_InputsAbAtThreshold0125_ProducesNandBitsWithRawPowers()
    {
        var table = await Extract(InputsAb, NandThreshold);

        table.Rows.Count.ShouldBe(4, "two logic inputs produce four rows");
        AssertRow(table, 0, expectedBit: true, expectedPower: RestingPower);
        AssertRow(table, 1, expectedBit: true, expectedPower: SingleInputPower);
        AssertRow(table, 2, expectedBit: true, expectedPower: SingleInputPower);
        AssertRow(table, 3, expectedBit: false, expectedPower: 0.0);
    }

    [Fact]
    public async Task TruthTable_WithoutBias_CannotInvert()
    {
        var group = await LoadGateGroup();
        var table = await new TruthTableExtractor().ExtractAsync(
            group, InputA, Outputs, Array.Empty<string>(), NotThreshold, WavelengthNm);

        table.BiasPinNames.ShouldBeEmpty();
        // Without the reference light the signal just passes — bright when A is on,
        // dark when A is off; an inversion needs the bias to take light away.
        AssertRow(table, 0, expectedBit: false, expectedPower: 0.0);
        AssertRow(table, 1, expectedBit: false, expectedPower: SingleInputPower);
    }

    [Fact]
    public async Task PanelVM_MarkingAAsInputYAsOutputBiasAsBias_ProducesNotTable()
    {
        var vm = await LoadPanelVmForGate();
        CheckPin(vm.InputPins, "A");
        CheckPin(vm.OutputPins, "Y");
        CheckPin(vm.BiasPins, "BIAS");
        vm.Threshold = NotThreshold;

        await vm.ExtractCommand.ExecuteAsync(null);

        vm.HasResult.ShouldBeTrue();
        vm.Rows.Count.ShouldBe(2, "NOT enumerates only A");
        AssertPanelRow(vm, "0", expectedBit: true, expectedPower: "0.50");
        AssertPanelRow(vm, "1", expectedBit: false, expectedPower: "0.25");
        vm.BiasText.ShouldContain("BIAS",
            customMessage: "the panel surfaces the bias assignment above the result");
    }

    [Fact]
    public async Task PanelVM_MarkingABAsInputsYAsOutputBiasAsBias_ProducesNandTable()
    {
        var vm = await LoadPanelVmForGate();
        CheckPin(vm.InputPins, "A");
        CheckPin(vm.InputPins, "B");
        CheckPin(vm.OutputPins, "Y");
        CheckPin(vm.BiasPins, "BIAS");
        vm.Threshold = NandThreshold;

        await vm.ExtractCommand.ExecuteAsync(null);

        vm.HasResult.ShouldBeTrue();
        vm.Rows.Count.ShouldBe(4, "NAND enumerates A and B");
        AssertPanelRow(vm, "0 0", expectedBit: true, expectedPower: "0.50");
        AssertPanelRow(vm, "1 0", expectedBit: true, expectedPower: "0.25");
        AssertPanelRow(vm, "0 1", expectedBit: true, expectedPower: "0.25");
        AssertPanelRow(vm, "1 1", expectedBit: false, expectedPower: "0.00",
            "the 135°-shifted BIAS cancels A·B exactly when both are on");
    }

    /// <summary>Asserts one row of the panel VM: input bits, output bit, formatted raw power.</summary>
    private static void AssertPanelRow(
        TruthTableViewModel vm, string inputBitsText, bool expectedBit, string expectedPower, string? because = null)
    {
        var row = vm.Rows.Single(r => r.InputBitsText == inputBitsText);
        row.OutputCells[0].IsOne.ShouldBe(expectedBit, because);
        row.OutputCells[0].PowerText.ShouldBe(expectedPower, because);
    }

    /// <summary>Loads the shipped example through the real load path and activates the panel VM for the group.</summary>
    private static async Task<TruthTableViewModel> LoadPanelVmForGate()
    {
        var library = new ObservableCollection<ComponentTemplate>(TestPdkLoader.LoadAllTemplates());
        var canvas = new DesignCanvasViewModel();
        var fileOps = new FileOperationsViewModel(
            canvas,
            new CommandManager(),
            new SimpleNazcaExporter(),
            new SaxExporter(),
            library,
            new GdsExportViewModel(new GdsExportService()),
            new PhotonTorchExportViewModel(new PhotonTorchExporter(), canvas),
            null!);

        var examplePath = Path.Combine(ExampleDesignFilesTests.ExamplesDirectory(), ExampleFileName);
        (await fileOps.LoadDesignFromPathAsync(examplePath)).ShouldBeTrue(
            $"the shipped example '{ExampleFileName}' must load through the real load path");

        var groupVm = canvas.Components.Single(c => c.Component is ComponentGroup);
        canvas.Selection.SelectSingle(groupVm);
        var vm = new TruthTableViewModel();
        vm.ConfigureForSelection(groupVm, canvas);
        vm.IsGroupSelected.ShouldBeTrue("the loaded example group activates the Truth Table panel");
        return vm;
    }

    /// <summary>Checks one pin in the given list (mirrors the panel's checkbox interaction).</summary>
    private static void CheckPin(ObservableCollection<PinSelectionViewModel> pins, string name) =>
        pins.Single(p => p.PinName == name).IsChecked = true;

    /// <summary>Loads the shipped example through the real load path and returns its group.</summary>
    private static async Task<ComponentGroup> LoadGateGroup()
    {
        var library = new ObservableCollection<ComponentTemplate>(TestPdkLoader.LoadAllTemplates());
        var canvas = new DesignCanvasViewModel();
        var fileOps = new FileOperationsViewModel(
            canvas,
            new CommandManager(),
            new SimpleNazcaExporter(),
            new SaxExporter(),
            library,
            new GdsExportViewModel(new GdsExportService()),
            new PhotonTorchExportViewModel(new PhotonTorchExporter(), canvas),
            null!);

        var examplePath = Path.Combine(ExampleDesignFilesTests.ExamplesDirectory(), ExampleFileName);
        (await fileOps.LoadDesignFromPathAsync(examplePath)).ShouldBeTrue(
            $"the shipped example '{ExampleFileName}' must load through the real load path");

        return canvas.Components.Select(c => c.Component).OfType<ComponentGroup>().Single();
    }

    /// <summary>Extracts the gate's truth table at the given analog→digital threshold.</summary>
    private static async Task<TruthTable> Extract(string[] inputs, double threshold)
    {
        var group = await LoadGateGroup();
        var table = await new TruthTableExtractor().ExtractAsync(
            group, inputs, Outputs, Biases, threshold, WavelengthNm);
        table.GroupName.ShouldBe(GroupName);
        table.InputPinNames.ShouldBe(inputs);
        table.OutputPinNames.ShouldBe(Outputs);
        return table;
    }

    /// <summary>Asserts one row's output bit and its raw simulated power behind it.</summary>
    private static void AssertRow(TruthTable table, int pattern, bool expectedBit, double expectedPower)
    {
        var row = table.Rows[pattern];
        var output = row.Outputs["Y"];
        output.IsOne.ShouldBe(expectedBit,
            $"threshold {table.PowerThreshold}: output bit for input pattern {pattern}");
        output.Power.ShouldBe(expectedPower, PowerTolerance,
            $"threshold {table.PowerThreshold}: raw power for input pattern {pattern}");
    }
}
