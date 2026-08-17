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

        var reloaded = await LoadFromDisk();
        var group = SingleGateGroup(reloaded);
        var saved = group.TruthTablePinAssignment.ShouldNotBeNull(
            "the extracted assignment must be restored on the reloaded group");
        saved.InputPinNames.ShouldBe(NandInputs);
        saved.OutputPinNames.ShouldBe(NandOutputs);
        saved.BiasPinNames.ShouldBe(NandBiases);
        saved.Threshold.ShouldBe(NandThreshold);
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
