using System.Collections.ObjectModel;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Export;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Analysis.LogicAnalysis;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Export;
using Moq;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Rung-4 assembly from a loaded design: two instances of the shipped
/// <c>examples/Logic Gate NOT-NAND.lun</c> gate — one read as NAND, one as NOT —
/// carry their pin roles and thresholds as persisted <see cref="TruthTablePinAssignment"/>s
/// delivered by the real extract → save → load path (the test never hands roles to the
/// assembler). Wired NAND.Y→NOT.A through a design connection, the
/// <see cref="LogicNetworkAssembler"/> turns the pair into an evaluator that reads the
/// AND truth table by pure table lookup. A group without a persisted assignment on the
/// same canvas is ignored, and a design without any gate group fails with a readable
/// error instead of an empty network.
/// </summary>
public class LogicNetworkAssemblerExampleTests : IDisposable
{
    private const string ExampleFileName = "Logic Gate NOT-NAND.lun";
    private const string NandGateId = "NAND";
    private const string NotGateId = "NOT";
    private const double NandThreshold = 0.125;
    private const double NotThreshold = 0.375;
    private const int WavelengthNm = 1550;

    private static readonly string[] NandInputs = { "A", "B" };
    private static readonly string[] NotInputs = { "A" };
    private static readonly string[] Outputs = { "Y" };
    private static readonly string[] Biases = { "BIAS" };

    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var path in _tempFiles.Where(File.Exists))
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AssembleAsync_PersistedNandAndNotWiredOnCanvas_EvaluatesTheAndTruthTable()
    {
        var nand = await LoadGateWithPersistedRoles(NandGateId, NandInputs, NandThreshold);
        var inv = await LoadGateWithPersistedRoles(NotGateId, NotInputs, NotThreshold);
        var connections = new[] { Connect(nand, "Y", inv, "A") };

        var network = await new LogicNetworkAssembler().AssembleAsync(
            new Component[] { nand, inv }, connections, WavelengthNm);

        network.InputPinNames.ShouldBe(new[] { "NAND.A", "NAND.B" },
            "the unconnected NAND inputs become the network-level inputs");
        network.OutputPinNames.ShouldBe(new[] { "NAND.Y", "NOT.Y" },
            "every gate output pin becomes a network-level output tap");
        foreach (var a in new[] { false, true })
        foreach (var b in new[] { false, true })
        {
            var outputs = network.Evaluate(Bits(("NAND.A", a), ("NAND.B", b)));
            outputs["NOT.Y"].ShouldBe(a && b, $"AND = NOT(NAND(A, B)) for A={a}, B={b}");
            outputs["NAND.Y"].ShouldBe(!(a && b),
                $"the driving gate output stays readable as a tap for A={a}, B={b}");
        }
    }

    [Fact]
    public async Task AssembleAsync_GroupWithoutAssignmentOnTheCanvas_IsIgnored()
    {
        var nand = await LoadGateWithPersistedRoles(NandGateId, NandInputs, NandThreshold);
        var plain = SingleGateGroup(await LoadGateOnCanvas());
        plain.GroupName = "PLAIN";
        plain.TruthTablePinAssignment = null;

        var network = await new LogicNetworkAssembler().AssembleAsync(
            new Component[] { nand, plain }, Array.Empty<WaveguideConnection>(), WavelengthNm);

        network.Gates.Keys.ShouldBe(new[] { NandGateId },
            "the group without a persisted assignment is not a gate");
        foreach (var a in new[] { false, true })
        foreach (var b in new[] { false, true })
        {
            network.Evaluate(Bits(("NAND.A", a), ("NAND.B", b)))["NAND.Y"]
                .ShouldBe(!(a && b), $"the remaining gate still evaluates for A={a}, B={b}");
        }
    }

    [Fact]
    public async Task AssembleAsync_DesignWithoutGateGroups_ThrowsAReadableError()
    {
        var plain = SingleGateGroup(await LoadGateOnCanvas());
        plain.TruthTablePinAssignment = null;

        var error = await Should.ThrowAsync<InvalidOperationException>(
            () => new LogicNetworkAssembler().AssembleAsync(
                new Component[] { plain }, Array.Empty<WaveguideConnection>(), WavelengthNm));

        error.Message.ShouldContain("no logic gate");
    }

    /// <summary>
    /// Delivers one gate group the way Jonas gets it: the shipped example is loaded,
    /// its truth table extracted through the real panel flow (which re-seeds the
    /// persisted assignment), saved, and reloaded — the reloaded group carries roles
    /// and threshold from the file, not from this test. The shipped file names the
    /// A/B signals since #1141; the flow under test predates signal identity, so the
    /// roles are re-extracted with unnamed pins like any pre-#1033 extraction.
    /// </summary>
    private async Task<ComponentGroup> LoadGateWithPersistedRoles(
        string gateId, string[] inputPinNames, double threshold)
    {
        var canvas = await LoadGateOnCanvas();
        await ExtractThroughPanel(canvas, inputPinNames, threshold);
        SingleGateGroup(canvas).GroupName = gateId;
        var path = NewTempFile();
        await Save(canvas, path);

        var group = SingleGateGroup(await LoadFromDisk(path));
        group.TruthTablePinAssignment.ShouldNotBeNull(
            "the reloaded group must carry the persisted roles — the assembler reads them from here");
        group.EnsureSMatrixComputed();
        return group;
    }

    /// <summary>
    /// Drives the loaded example through the Truth Table panel's ViewModel exactly as
    /// the interactive flow does — this is what populates the persisted assignment.
    /// </summary>
    private static async Task ExtractThroughPanel(
        DesignCanvasViewModel canvas, string[] inputPinNames, double threshold)
    {
        var groupVm = canvas.Components.Single(c => c.Component is ComponentGroup);
        canvas.Selection.SelectSingle(groupVm);
        var vm = new TruthTableViewModel();
        vm.ConfigureForSelection(groupVm, canvas);
        vm.IsGroupSelected.ShouldBeTrue("the loaded gate group must activate the panel");
        // The persisted roles prefill the checks: uncheck every input the reading
        // does not enumerate (the NOT reading drops B), then tick the wanted ones.
        foreach (var pin in vm.InputPins)
            pin.IsChecked = inputPinNames.Contains(pin.PinName);
        vm.OutputPins.Single(p => p.PinName == Outputs[0]).IsChecked = true;
        vm.BiasPins.Single(p => p.PinName == Biases[0]).IsChecked = true;
        // Clear the shipped A/B signal names (#1141): the pre-#1033 flow under test
        // assembles raw <gate>.<pin> input names, not signal-merged ones.
        foreach (var pin in vm.InputPins)
            pin.SignalName = "";
        vm.Threshold = threshold;
        await vm.ExtractCommand.ExecuteAsync(null);
        vm.HasResult.ShouldBeTrue("the extraction that seeds persistence must succeed");
    }

    /// <summary>Loads the shipped example through the real load path and returns the canvas.</summary>
    private static async Task<DesignCanvasViewModel> LoadGateOnCanvas()
    {
        var canvas = new DesignCanvasViewModel();
        var fileOps = CreateFileOperations(canvas);
        var examplePath = Path.Combine(ExampleDesignFilesTests.ExamplesDirectory(), ExampleFileName);
        (await fileOps.LoadDesignFromPathAsync(examplePath)).ShouldBeTrue(
            $"the shipped example '{ExampleFileName}' must load through the real load path");
        return canvas;
    }

    /// <summary>Saves the canvas's design through the real save path to a temp file.</summary>
    private static async Task Save(DesignCanvasViewModel canvas, string path)
    {
        var saveVm = CreateFileOperations(canvas);
        var saveDialog = new Mock<IFileDialogService>();
        saveDialog.Setup(f => f.ShowSaveFileDialogAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(path);
        saveVm.FileDialogService = saveDialog.Object;
        await saveVm.SaveDesignAsCommand.ExecuteAsync(null);
        File.Exists(path).ShouldBeTrue("the design file must be written");
    }

    /// <summary>Reloads a saved design through the real load path onto a fresh canvas.</summary>
    private static async Task<DesignCanvasViewModel> LoadFromDisk(string path)
    {
        var canvas = new DesignCanvasViewModel();
        var fileOps = CreateFileOperations(canvas);
        (await fileOps.LoadDesignFromPathAsync(path)).ShouldBeTrue(
            "the saved design must load through the real load path");
        return canvas;
    }

    /// <summary>The canvas wiring between two gate groups' external pins: NAND.Y → NOT.A.</summary>
    private static WaveguideConnection Connect(
        ComponentGroup from, string fromPin, ComponentGroup to, string toPin) =>
        new() { StartPin = Pin(from, fromPin), EndPin = Pin(to, toPin) };

    /// <summary>The group's connectable external pin, surfaced by the S-matrix sync.</summary>
    private static PhysicalPin Pin(ComponentGroup group, string name) =>
        group.PhysicalPins.Single(p => p.Name == name);

    private static ComponentGroup SingleGateGroup(DesignCanvasViewModel canvas) =>
        canvas.Components.Select(c => c.Component).OfType<ComponentGroup>().Single();

    private static FileOperationsViewModel CreateFileOperations(DesignCanvasViewModel canvas)
    {
        var library = new ObservableCollection<ComponentTemplate>(TestPdkLoader.LoadAllTemplates());
        return new FileOperationsViewModel(
            canvas,
            new CommandManager(),
            new SimpleNazcaExporter(),
            new SaxExporter(),
            library,
            new GdsExportViewModel(new GdsExportService()),
            new PhotonTorchExportViewModel(new PhotonTorchExporter(), canvas),
            null!);
    }

    private string NewTempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"logic-assembler-{Guid.NewGuid():N}.lun");
        _tempFiles.Add(path);
        return path;
    }

    /// <summary>Builds an input-bit dictionary from (name, bit) pairs.</summary>
    private static Dictionary<string, bool> Bits(params (string Name, bool Bit)[] bits) =>
        bits.ToDictionary(pair => pair.Name, pair => pair.Bit);
}
