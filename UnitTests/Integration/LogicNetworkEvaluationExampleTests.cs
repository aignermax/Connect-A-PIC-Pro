using System.Collections.ObjectModel;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
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
/// Rung-4 logic-layer evaluation over the real extracted tables of the shipped
/// <c>examples/Logic Gate NOT-NAND.lun</c> gate: the NAND and NOT truth tables
/// come out of the actual S-matrix simulation via <see cref="TruthTableExtractor"/>,
/// then <see cref="LogicGateModel"/> and <see cref="LogicNetworkEvaluator"/>
/// compose them into AND = NOT(NAND) and OR = NAND(NOT a, NOT b) — pure table
/// lookups, no re-simulation. Where the passive-linear two-stage cascade of the
/// AND-from-NAND example tops out at a 2× margin, the logic layer restores clean
/// bits at every stage, so the composed gates read exact truth tables.
/// </summary>
public class LogicNetworkEvaluationExampleTests
{
    private const string ExampleFileName = "Logic Gate NOT-NAND.lun";
    private const double NandThreshold = 0.125;
    private const double NotThreshold = 0.375;
    private const int WavelengthNm = 1550;

    private static readonly string[] InputsAb = { "A", "B" };
    private static readonly string[] InputA = { "A" };
    private static readonly string[] Biases = { "BIAS" };
    private static readonly string[] Outputs = { "Y" };

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public async Task LogicLayer_AndFromExtractedNandAndNot_MatchesTheAndTruthTable(
        bool a, bool b, bool expected)
    {
        var network = new LogicNetworkEvaluator(
            new[] { "a", "b" },
            new Dictionary<string, LogicGateModel>
            {
                ["nand"] = await ExtractGate(InputsAb, NandThreshold),
                ["inv"] = await ExtractGate(InputA, NotThreshold),
            },
            new Dictionary<LogicPinRef, LogicNetDriver>
            {
                [new LogicPinRef("nand", "A")] = new LogicNetDriver.NetworkInput("a"),
                [new LogicPinRef("nand", "B")] = new LogicNetDriver.NetworkInput("b"),
                [new LogicPinRef("inv", "A")] = new LogicNetDriver.GateOutput(new LogicPinRef("nand", "Y")),
            },
            new Dictionary<string, LogicPinRef> { ["y"] = new("inv", "Y") });

        network.Evaluate(new Dictionary<string, bool> { ["a"] = a, ["b"] = b })["y"].ShouldBe(expected);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task LogicLayer_OrFromExtractedGates_MatchesTheOrTruthTable(bool a, bool b, bool expected)
    {
        var network = new LogicNetworkEvaluator(
            new[] { "a", "b" },
            new Dictionary<string, LogicGateModel>
            {
                ["notA"] = await ExtractGate(InputA, NotThreshold),
                ["notB"] = await ExtractGate(InputA, NotThreshold),
                ["nand"] = await ExtractGate(InputsAb, NandThreshold),
            },
            new Dictionary<LogicPinRef, LogicNetDriver>
            {
                [new LogicPinRef("notA", "A")] = new LogicNetDriver.NetworkInput("a"),
                [new LogicPinRef("notB", "A")] = new LogicNetDriver.NetworkInput("b"),
                [new LogicPinRef("nand", "A")] = new LogicNetDriver.GateOutput(new LogicPinRef("notA", "Y")),
                [new LogicPinRef("nand", "B")] = new LogicNetDriver.GateOutput(new LogicPinRef("notB", "Y")),
            },
            new Dictionary<string, LogicPinRef> { ["y"] = new("nand", "Y") });

        network.Evaluate(new Dictionary<string, bool> { ["a"] = a, ["b"] = b })["y"].ShouldBe(expected);
    }

    /// <summary>
    /// Extracts the shipped example's truth table through the real simulation and
    /// wraps it as an evaluable logic-level gate model.
    /// </summary>
    private static async Task<LogicGateModel> ExtractGate(string[] inputs, double threshold)
    {
        var group = await LoadGateGroup();
        var table = await new TruthTableExtractor().ExtractAsync(
            group, inputs, Outputs, Biases, threshold, WavelengthNm);
        return LogicGateModel.FromTruthTable(table);
    }

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
}
