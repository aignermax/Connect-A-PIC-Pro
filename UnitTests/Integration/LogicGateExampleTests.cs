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
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Headless load test for the shipped logic-gate starter example
/// <c>examples/Logic Gate OR-AND.lun</c> (issue #958, rung 4 of the NAND game):
/// the file loads through the real load path as a single "OR/AND Gate" group
/// built only from bundled Demo PDK components, and the
/// <see cref="TruthTableExtractor"/> reads the OR table at threshold 0.25 and
/// the AND table at threshold 0.75 off the same circuit — the analog→digital
/// threshold lesson — with raw powers 0.0 / 0.5 / 0.5 / 1.0, mirroring
/// <see cref="Components.NandGameTruthTableJourneyTests"/>.
/// </summary>
public class LogicGateExampleTests
{
    private const string ExampleFileName = "Logic Gate OR-AND.lun";
    private const string GroupName = "OR/AND Gate";
    private const double PowerTolerance = 1e-6;
    private const int WavelengthNm = 1550;

    private static readonly string[] InputPins = { "A", "B" };
    private static readonly string[] OutputPins = { "Y" };

    [Fact]
    public async Task Example_LoadsAsSingleGroup_WithCleanExternalPins()
    {
        var group = await LoadGateGroup();

        group.GroupName.ShouldBe(GroupName);
        // The group description carries the threshold lesson (OR at 0.25, AND at 0.75).
        group.Description.ShouldContain("0.25");
        group.Description.ShouldContain("0.75");
        group.ChildComponents.Select(c => c.Identifier)
            .ShouldBe(new[] { "input_a", "input_b", "combiner", "output" }, ignoreOrder: true);
        group.InternalPaths.Count.ShouldBe(3,
            "both inputs and the combiner output are routed inside the group");
        group.ExternalPins.Select(p => p.Name)
            .ShouldBe(new[] { "A", "B", "AUX", "Y" }, ignoreOrder: true);
        group.ExternalPins.ShouldAllBe(p => p.InternalPin != null && p.InternalPin.LogicalPin != null,
            "every external pin stays bound to a simulatable component pin");
    }

    [Fact]
    public async Task TruthTable_Threshold025_ProducesOrBitsWithRawPowers()
    {
        var table = await Extract(threshold: 0.25);

        table.Rows.Count.ShouldBe(4, "two logic inputs produce four rows");
        AssertRow(table, a: false, b: false, expectedBit: false, expectedPower: 0.0);
        AssertRow(table, a: true, b: false, expectedBit: true, expectedPower: 0.5);
        AssertRow(table, a: false, b: true, expectedBit: true, expectedPower: 0.5);
        AssertRow(table, a: true, b: true, expectedBit: true, expectedPower: 1.0);
    }

    [Fact]
    public async Task TruthTable_Threshold075_ProducesAndBitsWithRawPowers()
    {
        var table = await Extract(threshold: 0.75);

        table.Rows.Count.ShouldBe(4, "two logic inputs produce four rows");
        AssertRow(table, a: false, b: false, expectedBit: false, expectedPower: 0.0);
        AssertRow(table, a: true, b: false, expectedBit: false, expectedPower: 0.5);
        AssertRow(table, a: false, b: true, expectedBit: false, expectedPower: 0.5);
        AssertRow(table, a: true, b: true, expectedBit: true, expectedPower: 1.0);
    }

    /// <summary>
    /// The persisted pin roles and signal names make the example build in the Logic
    /// panel without any manual role assignment: toggles A and B drive the gate and
    /// its output tap reads the OR table at the persisted threshold 0.25.
    /// </summary>
    [Fact]
    public async Task LogicPanel_Toggles_ReproduceOrTable()
    {
        var canvas = await LoadGateOnCanvas();
        var panel = new LogicPanelViewModel();
        panel.Configure(canvas);
        await panel.BuildNetworkCommand.ExecuteAsync(null);

        panel.HasNetwork.ShouldBeTrue(
            $"the persisted pin roles must assemble in the Logic panel: {panel.StatusText}");
        panel.Inputs.Select(i => i.PinName).ShouldBe(InputPins, ignoreOrder: true,
            customMessage: "the persisted signal names A and B become the panel toggles");
        panel.Outputs.Count.ShouldBe(1, "the single gate exposes exactly one output tap");

        EvaluatePanel(panel, a: false, b: false).ShouldBeFalse("OR: A=0 B=0 must read 0");
        EvaluatePanel(panel, a: true, b: false).ShouldBeTrue("OR: A=1 B=0 must read 1");
        EvaluatePanel(panel, a: false, b: true).ShouldBeTrue("OR: A=0 B=1 must read 1");
        EvaluatePanel(panel, a: true, b: true).ShouldBeTrue("OR: A=1 B=1 must read 1");
    }

    /// <summary>Sets the A/B toggles and returns the single output tap's evaluated bit.</summary>
    private static bool EvaluatePanel(LogicPanelViewModel panel, bool a, bool b)
    {
        panel.Inputs.Single(i => i.PinName == "A").IsOn = a;
        panel.Inputs.Single(i => i.PinName == "B").IsOn = b;
        return panel.Outputs.Single().IsOne;
    }

    /// <summary>Loads the shipped example through the real load path and returns its group.</summary>
    private static async Task<ComponentGroup> LoadGateGroup()
    {
        var canvas = await LoadGateOnCanvas();
        return canvas.Components.Select(c => c.Component).OfType<ComponentGroup>().Single();
    }

    /// <summary>Loads the shipped example through the real load path and returns the canvas.</summary>
    private static async Task<DesignCanvasViewModel> LoadGateOnCanvas()
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

        return canvas;
    }

    /// <summary>Extracts the gate's truth table at the given analog→digital threshold.</summary>
    private static async Task<TruthTable> Extract(double threshold)
    {
        var group = await LoadGateGroup();
        var table = await new TruthTableExtractor().ExtractAsync(
            group, InputPins, OutputPins, threshold, WavelengthNm);
        table.GroupName.ShouldBe(GroupName);
        table.InputPinNames.ShouldBe(InputPins);
        table.OutputPinNames.ShouldBe(OutputPins);
        return table;
    }

    /// <summary>Asserts one row's output bit and its raw simulated power behind it.</summary>
    private static void AssertRow(
        TruthTable table, bool a, bool b, bool expectedBit, double expectedPower)
    {
        var row = table.Rows.Single(r => r.InputBits["A"] == a && r.InputBits["B"] == b);
        var output = row.Outputs["Y"];
        var pattern = $"A={(a ? 1 : 0)} B={(b ? 1 : 0)}";
        output.IsOne.ShouldBe(expectedBit,
            $"threshold {table.PowerThreshold}: output bit for {pattern}");
        output.Power.ShouldBe(expectedPower, PowerTolerance,
            $"threshold {table.PowerThreshold}: raw power for {pattern}");
    }
}
