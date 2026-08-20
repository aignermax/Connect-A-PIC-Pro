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
/// Persistence round-trip of the Truth Table pin-role assignment in the .lun file
/// (issue #981): whatever the panel last successfully extracted with — input pins
/// in bit order, output pins, bias pins, threshold — survives save → load, the panel
/// prefills on group selection, and extraction reproduces the pinned table without
/// any manual role re-assignment. Legacy files without the new block keep loading
/// exactly as before.
/// </summary>
public class TruthTablePinAssignmentPersistenceTests : IDisposable
{
    private const string ExampleFileName = "Logic Gate NOT-NAND.lun";
    private const double NandThreshold = 0.125;
    private const int WavelengthNm = 1550;
    private static readonly string[] NandInputs = { "A", "B" };
    private static readonly string[] NandOutputs = { "Y" };
    private static readonly string[] NandBiases = { "BIAS" };

    private readonly string _designFilePath =
        Path.Combine(Path.GetTempPath(), $"truthtable-persist-{Guid.NewGuid():N}.lun");

    public void Dispose()
    {
        if (File.Exists(_designFilePath))
            File.Delete(_designFilePath);
    }

    [Fact]
    public async Task RoundTrip_ExtractedAssignmentSurvivesSaveAndReload()
    {
        var canvas = await LoadGateOnCanvas();
        await ExtractNandThroughPanel(canvas);

        await Save(canvas);
        var fileText = File.ReadAllText(_designFilePath);
        fileText.ShouldContain("TruthTablePinAssignment", Case.Sensitive,
            "the persisted .lun carries the truth-table block after an extraction");
        fileText.ShouldContain("\"Threshold\": 0.125", Case.Sensitive);
        fileText.ShouldNotContain("InputSignalNames", Case.Sensitive,
            "an extraction without signal names writes no signal block — the legacy format stays byte-clean (#1025)");

        var reloaded = await LoadFromDisk();
        var group = SingleGateGroup(reloaded);
        var saved = group.TruthTablePinAssignment.ShouldNotBeNull(
            "the extracted assignment must be restored on the reloaded group");
        saved.InputPinNames.ShouldBe(NandInputs);
        saved.OutputPinNames.ShouldBe(NandOutputs);
        saved.BiasPinNames.ShouldBe(NandBiases);
        saved.Threshold.ShouldBe(NandThreshold);
        saved.InputSignalNames.ShouldBeNull(
            "no pin carried a signal name — nothing is invented on load (#1025)");
    }

    [Fact]
    public async Task RoundTrip_SignalNamesSurviveSaveAndReload()
    {
        // Issue #1025: the network-signal identity assigned to input pins is part of
        // the persisted role block and must ride through save → load like the roles.
        var canvas = await LoadGateOnCanvas();
        await ExtractNandThroughPanel(canvas);
        SingleGateGroup(canvas).TruthTablePinAssignment!.InputSignalNames = new()
        {
            ["A"] = "OperandA",
            ["B"] = "OperandB",
        };

        await Save(canvas);
        File.ReadAllText(_designFilePath).ShouldContain("InputSignalNames", Case.Sensitive);

        var reloaded = await LoadFromDisk();
        var saved = SingleGateGroup(reloaded).TruthTablePinAssignment.ShouldNotBeNull();
        saved.InputSignalNames.ShouldBe(new Dictionary<string, string>
        {
            ["A"] = "OperandA",
            ["B"] = "OperandB",
        });
    }

    [Fact]
    public async Task RoundTrip_SignalNamesAssignedThroughPanel_SurviveSaveAndReload()
    {
        // Issue #1033: the user names the input pins' signals in the Truth Table
        // panel after extraction; the names persist like the roles.
        var canvas = await LoadGateOnCanvas();
        await ExtractNandThroughPanel(canvas);

        var groupVm = canvas.Components.Single(c => c.Component is ComponentGroup);
        canvas.Selection.SelectSingle(groupVm);
        var vm = new TruthTableViewModel();
        vm.ConfigureForSelection(groupVm, canvas);
        vm.SignalNamesVisible.ShouldBeTrue("after extraction the input rows offer the signal field");
        vm.InputPins.Single(p => p.PinName == "A").SignalName = "OperandA";
        vm.InputPins.Single(p => p.PinName == "B").SignalName = "OperandB";
        SingleGateGroup(canvas).TruthTablePinAssignment!.InputSignalNames.ShouldBe(
            new Dictionary<string, string> { ["A"] = "OperandA", ["B"] = "OperandB" },
            "the panel edit writes straight into the persisted assignment");

        await Save(canvas);
        File.ReadAllText(_designFilePath).ShouldContain("OperandA", Case.Sensitive);

        var reloaded = await LoadFromDisk();
        var saved = SingleGateGroup(reloaded).TruthTablePinAssignment.ShouldNotBeNull();
        saved.InputSignalNames.ShouldBe(new Dictionary<string, string>
        {
            ["A"] = "OperandA",
            ["B"] = "OperandB",
        });

        // …and the reopened panel prefills the signal fields from the file.
        var reloadedGroupVm = reloaded.Components.Single(c => c.Component is ComponentGroup);
        reloaded.Selection.SelectSingle(reloadedGroupVm);
        var reloadedVm = new TruthTableViewModel();
        reloadedVm.ConfigureForSelection(reloadedGroupVm, reloaded);
        reloadedVm.SignalNamesVisible.ShouldBeTrue();
        reloadedVm.InputPins.Single(p => p.PinName == "A").SignalName.ShouldBe("OperandA");
        reloadedVm.InputPins.Single(p => p.PinName == "B").SignalName.ShouldBe("OperandB");
    }

    [Fact]
    public async Task RoundTrip_OutputSignalNamesSurviveSaveAndReload()
    {
        // Issue #1046: the signal name assigned to an output pin is part of the
        // persisted role block and must ride through save → load like the roles.
        var canvas = await LoadGateOnCanvas();
        await ExtractNandThroughPanel(canvas);
        SingleGateGroup(canvas).TruthTablePinAssignment!.OutputSignalNames = new()
        {
            ["Y"] = "Done",
        };

        await Save(canvas);
        File.ReadAllText(_designFilePath).ShouldContain("OutputSignalNames", Case.Sensitive);

        var reloaded = await LoadFromDisk();
        var saved = SingleGateGroup(reloaded).TruthTablePinAssignment.ShouldNotBeNull();
        saved.OutputSignalNames.ShouldBe(new Dictionary<string, string> { ["Y"] = "Done" });
    }

    [Fact]
    public async Task RoundTrip_OutputSignalNamesAssignedThroughPanel_SurviveSaveAndReload()
    {
        // Issue #1046: the user names the output pin's signal in the Truth Table
        // panel after extraction; the name persists like the roles.
        var canvas = await LoadGateOnCanvas();
        await ExtractNandThroughPanel(canvas);

        var groupVm = canvas.Components.Single(c => c.Component is ComponentGroup);
        canvas.Selection.SelectSingle(groupVm);
        var vm = new TruthTableViewModel();
        vm.ConfigureForSelection(groupVm, canvas);
        vm.SignalNamesVisible.ShouldBeTrue("after extraction the output rows offer the signal field");
        vm.OutputPins.Single(p => p.PinName == "Y").SignalName = "Done";
        SingleGateGroup(canvas).TruthTablePinAssignment!.OutputSignalNames.ShouldBe(
            new Dictionary<string, string> { ["Y"] = "Done" },
            "the panel edit writes straight into the persisted assignment");

        await Save(canvas);
        File.ReadAllText(_designFilePath).ShouldContain("Done", Case.Sensitive);

        var reloaded = await LoadFromDisk();
        var saved = SingleGateGroup(reloaded).TruthTablePinAssignment.ShouldNotBeNull();
        saved.OutputSignalNames.ShouldBe(new Dictionary<string, string> { ["Y"] = "Done" });

        // …and the reopened panel prefills the output signal field from the file.
        var reloadedGroupVm = reloaded.Components.Single(c => c.Component is ComponentGroup);
        reloaded.Selection.SelectSingle(reloadedGroupVm);
        var reloadedVm = new TruthTableViewModel();
        reloadedVm.ConfigureForSelection(reloadedGroupVm, reloaded);
        reloadedVm.SignalNamesVisible.ShouldBeTrue();
        reloadedVm.OutputPins.Single(p => p.PinName == "Y").SignalName.ShouldBe("Done");
    }

    [Fact]
    public async Task RoundTrip_LastOutputSignalNameCleared_PersistsNoOutputSignalBlock()
    {
        // Issue #1046: clearing the last output signal field collapses the map to
        // null, so the saved .lun carries no output-signal block — legacy files
        // stay byte-clean.
        var canvas = await LoadGateOnCanvas();
        await ExtractNandThroughPanel(canvas);
        var groupVm = canvas.Components.Single(c => c.Component is ComponentGroup);
        canvas.Selection.SelectSingle(groupVm);
        var vm = new TruthTableViewModel();
        vm.ConfigureForSelection(groupVm, canvas);
        var pinY = vm.OutputPins.Single(p => p.PinName == "Y");
        pinY.SignalName = "Done";
        pinY.SignalName = "";
        SingleGateGroup(canvas).TruthTablePinAssignment!.OutputSignalNames.ShouldBeNull();

        await Save(canvas);
        var fileText = File.ReadAllText(_designFilePath);
        fileText.ShouldContain("TruthTablePinAssignment", Case.Sensitive);
        fileText.ShouldNotContain("OutputSignalNames", Case.Sensitive,
            "an emptied output-signal map persists as null — no format noise (#1046)");
    }

    [Fact]
    public async Task ReExtraction_PreservesOutputSignalNamesOfPinsThatStayOutputs()
    {
        // Issue #1046: re-extracting a gate's truth table rewrites the persisted
        // assignment — the signal names of pins that stay outputs must ride along.
        var canvas = await LoadGateOnCanvas();
        await ExtractNandThroughPanel(canvas);
        SingleGateGroup(canvas).TruthTablePinAssignment!.OutputSignalNames = new()
        {
            ["Y"] = "Done",
        };

        var groupVm = canvas.Components.Single(c => c.Component is ComponentGroup);
        canvas.Selection.SelectSingle(groupVm);
        var vm = new TruthTableViewModel();
        vm.ConfigureForSelection(groupVm, canvas);
        await vm.ExtractCommand.ExecuteAsync(null);
        vm.HasResult.ShouldBeTrue("the re-extraction must succeed");

        var saved = SingleGateGroup(canvas).TruthTablePinAssignment.ShouldNotBeNull();
        saved.OutputSignalNames.ShouldBe(new Dictionary<string, string> { ["Y"] = "Done" },
            "output signal names of pins that stay outputs survive the re-extraction");
    }

    [Fact]
    public async Task RoundTrip_LastSignalNameCleared_PersistsNoSignalBlock()
    {
        // Issue #1033: clearing the last signal field collapses the map to null, so
        // the saved .lun carries no signal block — legacy files stay byte-clean.
        var canvas = await LoadGateOnCanvas();
        await ExtractNandThroughPanel(canvas);
        var groupVm = canvas.Components.Single(c => c.Component is ComponentGroup);
        canvas.Selection.SelectSingle(groupVm);
        var vm = new TruthTableViewModel();
        vm.ConfigureForSelection(groupVm, canvas);
        var pinA = vm.InputPins.Single(p => p.PinName == "A");
        pinA.SignalName = "OperandA";
        pinA.SignalName = "";
        SingleGateGroup(canvas).TruthTablePinAssignment!.InputSignalNames.ShouldBeNull();

        await Save(canvas);
        var fileText = File.ReadAllText(_designFilePath);
        fileText.ShouldContain("TruthTablePinAssignment", Case.Sensitive);
        fileText.ShouldNotContain("InputSignalNames", Case.Sensitive,
            "an emptied signal map persists as null — no format noise (#1033)");
    }

    [Fact]
    public async Task ReExtraction_PreservesSignalNamesOfPinsThatStayInputs()
    {
        // Issue #1025: re-extracting a gate's truth table rewrites the persisted
        // assignment — the signal names of pins that stay inputs must ride along,
        // or every re-extraction would silently strip the design's signal identity.
        var canvas = await LoadGateOnCanvas();
        await ExtractNandThroughPanel(canvas);
        SingleGateGroup(canvas).TruthTablePinAssignment!.InputSignalNames = new()
        {
            ["A"] = "OperandA",
            ["B"] = "OperandB",
        };

        var groupVm = canvas.Components.Single(c => c.Component is ComponentGroup);
        canvas.Selection.SelectSingle(groupVm);
        var vm = new TruthTableViewModel();
        vm.ConfigureForSelection(groupVm, canvas);
        await vm.ExtractCommand.ExecuteAsync(null);
        vm.HasResult.ShouldBeTrue("the re-extraction must succeed");

        var saved = SingleGateGroup(canvas).TruthTablePinAssignment.ShouldNotBeNull();
        saved.InputSignalNames.ShouldBe(new Dictionary<string, string>
        {
            ["A"] = "OperandA",
            ["B"] = "OperandB",
        }, "signal names of pins that stay inputs survive the re-extraction");
    }

    [Fact]
    public async Task LegacyFile_WithoutTruthTableBlock_LoadsWithNullAssignment()
    {
        // The shipped example predates the persistence block — it is the legacy case.
        var canvas = await LoadGateOnCanvas();
        SingleGateGroup(canvas).TruthTablePinAssignment.ShouldBeNull(
            "legacy .lun files load with no truth-table assignment attached");

        // Saving and reloading the untouched legacy design must not invent a block:
        // no assignment → nothing persisted, no format noise.
        await Save(canvas);
        var fileText = File.ReadAllText(_designFilePath);
        fileText.ShouldNotContain("TruthTablePinAssignment", Case.Sensitive,
            "a never-extracted group persists no truth-table block");

        var reloaded = await LoadFromDisk();
        SingleGateGroup(reloaded).TruthTablePinAssignment.ShouldBeNull();
    }

    [Fact]
    public async Task RoundTrip_RegisterDesignationSurvivesSaveLoadAndReassembly()
    {
        // Issue #1086: the register flag rides in the persisted pin-role block like
        // the roles themselves, and the reloaded designation reaches the assembled
        // network as committed register state.
        var canvas = await LoadGateOnCanvas();
        await ExtractNandThroughPanel(canvas);
        SingleGateGroup(canvas).TruthTablePinAssignment!.IsRegister = true;

        await Save(canvas);
        File.ReadAllText(_designFilePath).ShouldContain("\"IsRegister\": true", Case.Sensitive);

        var reloaded = await LoadFromDisk();
        var group = SingleGateGroup(reloaded);
        group.TruthTablePinAssignment.ShouldNotBeNull().IsRegister.ShouldBeTrue(
            "the register designation must be restored on the reloaded group");

        group.EnsureSMatrixComputed();
        var network = await new LogicNetworkAssembler().AssembleAsync(
            new Component[] { group }, Array.Empty<WaveguideConnection>(), WavelengthNm);
        network.RegisterState.Keys.ShouldBe(new[] { new LogicPinRef(group.GroupName, "Y") },
            "the reloaded designation designates the register in the assembled network");
    }

    [Fact]
    public async Task RoundTrip_GateWithoutRegisterDesignation_PersistsNoRegisterFlag()
    {
        // Issue #1086: a plain combinational gate writes no register flag — the
        // default false is omitted, keeping the .lun format free of unused blocks.
        var canvas = await LoadGateOnCanvas();
        await ExtractNandThroughPanel(canvas);

        await Save(canvas);
        var fileText = File.ReadAllText(_designFilePath);
        fileText.ShouldContain("TruthTablePinAssignment", Case.Sensitive);
        fileText.ShouldNotContain("IsRegister", Case.Sensitive,
            "a plain gate persists no register flag — legacy files stay byte-clean");
    }

    [Fact]
    public async Task ReloadedGroup_PanelPrefillsRoles_AndExtractsNandTableWithoutManualAssignment()
    {
        var canvas = await LoadGateOnCanvas();
        await ExtractNandThroughPanel(canvas);
        await Save(canvas);
        var reloaded = await LoadFromDisk();

        // The panel Jonas sees after reopening the file: every role and the threshold
        // already set, before he touches a single checkbox.
        var groupVm = reloaded.Components.Single(c => c.Component is ComponentGroup);
        reloaded.Selection.SelectSingle(groupVm);
        var vm = new TruthTableViewModel();
        vm.ConfigureForSelection(groupVm, reloaded);

        vm.IsGroupSelected.ShouldBeTrue();
        vm.InputPins.Where(p => p.IsChecked).Select(p => p.PinName).ShouldBe(NandInputs);
        vm.OutputPins.Where(p => p.IsChecked).Select(p => p.PinName).ShouldBe(NandOutputs);
        vm.BiasPins.Where(p => p.IsChecked).Select(p => p.PinName).ShouldBe(NandBiases);
        vm.Threshold.ShouldBe(NandThreshold);

        // Extract with no further interaction — the pinned NAND table must come back.
        await vm.ExtractCommand.ExecuteAsync(null);
        vm.HasResult.ShouldBeTrue();
        vm.InputHeaders.ShouldBe(NandInputs);
        vm.OutputHeaders.ShouldBe(NandOutputs);
        vm.BiasSummaryText.ShouldContain("BIAS");
        vm.Rows.Count.ShouldBe(4);
        AssertPanelRow(vm, "0 0", expectedBit: true, expectedPowerText: "0.50");
        AssertPanelRow(vm, "1 0", expectedBit: true, expectedPowerText: "0.25");
        AssertPanelRow(vm, "0 1", expectedBit: true, expectedPowerText: "0.25");
        AssertPanelRow(vm, "1 1", expectedBit: false, expectedPowerText: "0.00");
    }

    /// <summary>Asserts one panel row's output bit and its displayed raw power.</summary>
    private static void AssertPanelRow(
        TruthTableViewModel vm, string inputBitsText, bool expectedBit, string expectedPowerText)
    {
        var row = vm.Rows.Single(r => r.InputBitsText == inputBitsText);
        row.OutputCells[0].IsOne.ShouldBe(expectedBit,
            $"threshold {vm.Threshold}: output bit for input pattern {inputBitsText}");
        row.OutputCells[0].PowerText.ShouldBe(expectedPowerText,
            $"threshold {vm.Threshold}: raw power for input pattern {inputBitsText}");
    }

    /// <summary>
    /// Drives the loaded example through the Truth Table panel's ViewModel exactly as
    /// the interactive flow does — this is what populates the persisted assignment.
    /// </summary>
    private static async Task ExtractNandThroughPanel(DesignCanvasViewModel canvas)
    {
        var groupVm = canvas.Components.Single(c => c.Component is ComponentGroup);
        canvas.Selection.SelectSingle(groupVm);
        var vm = new TruthTableViewModel();
        vm.ConfigureForSelection(groupVm, canvas);
        vm.IsGroupSelected.ShouldBeTrue("the loaded gate group must activate the panel");
        foreach (var name in NandInputs)
            vm.InputPins.Single(p => p.PinName == name).IsChecked = true;
        vm.OutputPins.Single(p => p.PinName == "Y").IsChecked = true;
        vm.BiasPins.Single(p => p.PinName == "BIAS").IsChecked = true;
        vm.Threshold = NandThreshold;
        await vm.ExtractCommand.ExecuteAsync(null);
        vm.HasResult.ShouldBeTrue("the manual extraction that seeds persistence must succeed");
    }

    /// <summary>Saves the canvas's design through the real save path to the temp file.</summary>
    private async Task Save(DesignCanvasViewModel canvas)
    {
        var saveVm = CreateFileOperations(canvas);
        var saveDialog = new Mock<IFileDialogService>();
        saveDialog.Setup(f => f.ShowSaveFileDialogAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(_designFilePath);
        saveVm.FileDialogService = saveDialog.Object;
        await saveVm.SaveDesignAsCommand.ExecuteAsync(null);
        File.Exists(_designFilePath).ShouldBeTrue("the design file must be written");
    }

    /// <summary>Reloads the temp file through the real load path onto a fresh canvas.</summary>
    private async Task<DesignCanvasViewModel> LoadFromDisk()
    {
        var canvas = new DesignCanvasViewModel();
        var loadVm = CreateFileOperations(canvas);
        (await loadVm.LoadDesignFromPathAsync(_designFilePath)).ShouldBeTrue(
            "the saved design must load through the real load path");
        return canvas;
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
}
