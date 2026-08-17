using System.Collections.ObjectModel;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Export;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Analysis.LogicAnalysis;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Export;
using Shouldly;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Rung-4 canvas-to-logic integration: two instances of the shipped
/// <c>examples/Logic Gate NOT-NAND.lun</c> gate are placed as top-level groups and
/// wired NAND→NOT through a design connection between their external pins, exactly
/// as Jonas routes them on the canvas. <see cref="LogicNetworkBuilder"/> derives the
/// network from those connections — the role assignments stand in for the #981
/// persistence data — and the evaluator reads the AND truth table by pure table
/// lookup: no re-simulation of the cascade, ideal level restoration at both stages.
/// </summary>
public class LogicNetworkBuilderExampleTests
{
    private const string ExampleFileName = "Logic Gate NOT-NAND.lun";
    private const string NandGateId = "NAND";
    private const string NotGateId = "NOT";
    private const double NandThreshold = 0.125;
    private const double NotThreshold = 0.375;
    private const int WavelengthNm = 1550;

    private static readonly string[] NandInputs = { "A", "B" };
    private static readonly string[] NotInputs = { "A" };
    private static readonly string[] Biases = { "BIAS" };
    private static readonly string[] Outputs = { "Y" };

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public async Task CanvasWiring_NandGroupFeedingNotGroup_EvaluatesTheAndTruthTable(
        bool a, bool b, bool expected)
    {
        var nand = await LoadGateInstance(NandGateId, NandInputs, NandThreshold);
        var inv = await LoadGateInstance(NotGateId, NotInputs, NotThreshold);
        var connections = new[] { ConnectGroups(nand.Group, "Y", inv.Group, "A") };

        var network = new LogicNetworkBuilder().Build(new[] { nand, inv }, connections);

        network.InputPinNames.ShouldBe(new[] { "NAND.A", "NAND.B" },
            "the unconnected NAND inputs become the network-level inputs");
        network.OutputPinNames.ShouldBe(new[] { "NAND.Y", "NOT.Y" },
            "every gate output pin becomes a network-level output tap");
        var inputBits = new Dictionary<string, bool> { ["NAND.A"] = a, ["NAND.B"] = b };
        network.Evaluate(inputBits)["NOT.Y"].ShouldBe(expected, "AND = NOT(NAND(A, B))");
        network.Evaluate(inputBits)["NAND.Y"].ShouldBe(!(a && b),
            "the driving gate output stays readable as a tap");
    }

    /// <summary>
    /// Loads the shipped example through the real load path, renames the group to its
    /// network-local gate id, and extracts its logic model through the real simulation.
    /// </summary>
    private static async Task<LogicGateInstance> LoadGateInstance(
        string gateId, string[] inputPinNames, double threshold)
    {
        var group = await LoadGateGroup();
        group.GroupName = gateId;
        var table = await new TruthTableExtractor().ExtractAsync(
            group, inputPinNames, Outputs, Biases, threshold, WavelengthNm);
        return new LogicGateInstance(
            group,
            LogicGateModel.FromTruthTable(table),
            new GateRoleAssignment(inputPinNames, Outputs, Biases, threshold));
    }

    /// <summary>The canvas wiring between two gate groups' external pins: NAND.Y → NOT.A.</summary>
    private static WaveguideConnection ConnectGroups(
        ComponentGroup from, string fromPin, ComponentGroup to, string toPin) =>
        new() { StartPin = ExternalPin(from, fromPin), EndPin = ExternalPin(to, toPin) };

    /// <summary>The group's connectable external pin, synced onto the group by the extraction.</summary>
    private static PhysicalPin ExternalPin(ComponentGroup group, string name) =>
        group.PhysicalPins.Single(p => p.Name == name);

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
