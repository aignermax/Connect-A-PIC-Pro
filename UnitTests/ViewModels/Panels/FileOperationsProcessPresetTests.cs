using System.Collections.ObjectModel;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Export;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP.Avalonia.ViewModels.Process;
using CAP_Core.Components.Process;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.ViewModels.Panels;

/// <summary>
/// Verifies that a fabrication process picked as a PDK preset — including design-specific
/// overrides — round-trips through the .lun file (issue #696), and that legacy files
/// without preset data keep loading exactly as before.
/// </summary>
public class FileOperationsProcessPresetTests
{
    private readonly ObservableCollection<ComponentTemplate> _library =
        new(TestPdkLoader.LoadAllTemplates());

    private static ActiveProcessSelection SinSelection() => new(
        "CornerStone SiN 300nm",
        new ProcessFingerprint("SiN", 300, "SiO2", 1550, "CornerStone SiN 300nm"),
        new[] { "CornerStone SiN" }, IsPlayground: false);

    private static ProcessPropertyOverrideData WidthOverride() => new()
    {
        Section = ProcessPropertyOverrideData.XsectionsSection,
        RowName = "xs_nc",
        Property = "WidthUm",
        Value = "1.5",
    };

    [Fact]
    public async Task PresetAndOverrides_SaveLoad_RoundTrip()
    {
        var saveVm = CreateFileOperations();
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_preset_process_{Guid.NewGuid()}.lun");

        try
        {
            saveVm.SetActiveProcess(SinSelection());
            saveVm.SetActiveProcessPreset("CornerStone SiN", new[] { WidthOverride() });
            await SaveTo(saveVm, tempFile);

            var loadVm = CreateFileOperations();
            await LoadFrom(loadVm, tempFile);

            loadVm.ActiveProcess.ShouldNotBeNull();
            loadVm.ActiveProcess!.DisplayName.ShouldBe("CornerStone SiN 300nm");
            loadVm.ActiveProcessPresetPdkName.ShouldBe("CornerStone SiN");
            var o = loadVm.ActiveProcessOverrides.ShouldHaveSingleItem();
            o.Section.ShouldBe(ProcessPropertyOverrideData.XsectionsSection);
            o.RowName.ShouldBe("xs_nc");
            o.Property.ShouldBe("WidthUm");
            o.Value.ShouldBe("1.5");
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ProcessWithoutPreset_SaveLoad_HasNoPresetData()
    {
        var saveVm = CreateFileOperations();
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_no_preset_{Guid.NewGuid()}.lun");

        try
        {
            saveVm.SetActiveProcess(SinSelection());
            await SaveTo(saveVm, tempFile);

            var loadVm = CreateFileOperations();
            await LoadFrom(loadVm, tempFile);

            loadVm.ActiveProcess.ShouldNotBeNull();
            loadVm.ActiveProcessPresetPdkName.ShouldBeNull(
                "a process chosen outside the preset flow must not gain a preset reference");
            loadVm.ActiveProcessOverrides.ShouldBeEmpty();
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void SetActiveProcess_ClearsAnyEarlierPresetReference()
    {
        var vm = CreateFileOperations();
        vm.SetActiveProcess(SinSelection());
        vm.SetActiveProcessPreset("CornerStone SiN", new[] { WidthOverride() });

        vm.SetActiveProcess(ActiveProcessSelection.Playground());

        vm.ActiveProcessPresetPdkName.ShouldBeNull(
            "a selection made outside the preset flow invalidates the stored preset");
        vm.ActiveProcessOverrides.ShouldBeEmpty();
    }

    [Fact]
    public void SetActiveProcessPreset_MarksTheDesignDirty()
    {
        var vm = CreateFileOperations();
        vm.HasUnsavedChanges.ShouldBeFalse();

        vm.SetActiveProcessPreset("CornerStone SiN", Array.Empty<ProcessPropertyOverrideData>());

        vm.HasUnsavedChanges.ShouldBeTrue();
    }

    private static async Task SaveTo(FileOperationsViewModel vm, string path)
    {
        var dialog = new Mock<IFileDialogService>();
        dialog.Setup(f => f.ShowSaveFileDialogAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(path);
        vm.FileDialogService = dialog.Object;
        await vm.SaveDesignAsCommand.ExecuteAsync(null);
    }

    private static async Task LoadFrom(FileOperationsViewModel vm, string path)
    {
        var dialog = new Mock<IFileDialogService>();
        dialog.Setup(f => f.ShowOpenFileDialogAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(path);
        vm.FileDialogService = dialog.Object;
        await vm.LoadDesignCommand.ExecuteAsync(null);
    }

    /// <summary>Mirrors the setup helper in <c>FileOperationsActiveProcessTests</c>.</summary>
    private FileOperationsViewModel CreateFileOperations()
    {
        var canvas = new DesignCanvasViewModel();
        var gdsExport = new GdsExportViewModel(new CAP_Core.Export.GdsExportService());
        var photonTorchExport = new PhotonTorchExportViewModel(
            new CAP_Core.Export.PhotonTorchExporter(), canvas);

        return new FileOperationsViewModel(
            canvas, new CommandManager(), new SimpleNazcaExporter(), new CAP_Core.Export.SaxExporter(),
            _library, gdsExport, photonTorchExport, null!, null);
    }
}
