using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Export;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using Moq;
using Shouldly;
using System.Collections.ObjectModel;

namespace UnitTests.Persistence;

/// <summary>
/// Save/load roundtrip for multi-parameter components: every slider
/// value (not just slider 0) must survive a .lun cycle, and the legacy
/// single-value field must still restore old files.
/// </summary>
public class MultiSliderPersistenceTests
{
    private readonly ObservableCollection<ComponentTemplate> _library =
        new(TestPdkLoader.LoadAllTemplates());

    [Fact]
    public async Task Mmi_BothParameterValues_SurviveSaveLoadRoundtrip()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"multislider_{Guid.NewGuid():N}.lun");
        try
        {
            var (saveVm, saveCanvas) = CreateSetup();
            var template = _library.First(t => t.Name == "1x2 MMI Splitter");
            var component = ComponentTemplates.CreateFromTemplate(template, 100, 100);
            component.Identifier = "mmi_under_test";
            saveCanvas.AddComponent(component, template.Name);

            component.GetSlider(0)!.Value = 1.7;  // insertion loss [dB]
            component.GetSlider(1)!.Value = 72.5; // splitting ratio [%]

            await SaveToFile(saveVm, tempFile);

            var (loadVm, loadCanvas) = CreateSetup();
            await LoadFromFile(loadVm, tempFile);

            var loaded = loadCanvas.Components
                .First(c => c.Component.Identifier == "mmi_under_test").Component;
            loaded.GetSlider(0)!.Value.ShouldBe(1.7, 1e-9, "insertion loss must roundtrip");
            loaded.GetSlider(1)!.Value.ShouldBe(72.5, 1e-9, "splitting ratio must roundtrip");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task TwoMmiInstances_KeepTheirOwnValues_AfterRoundtrip()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"multislider_{Guid.NewGuid():N}.lun");
        try
        {
            var (saveVm, saveCanvas) = CreateSetup();
            var template = _library.First(t => t.Name == "1x2 MMI Splitter");

            var a = ComponentTemplates.CreateFromTemplate(template, 0, 0);
            a.Identifier = "mmi_a";
            saveCanvas.AddComponent(a, template.Name);
            a.GetSlider(1)!.Value = 10;

            var b = ComponentTemplates.CreateFromTemplate(template, 600, 0);
            b.Identifier = "mmi_b";
            saveCanvas.AddComponent(b, template.Name);
            b.GetSlider(1)!.Value = 90;

            await SaveToFile(saveVm, tempFile);

            var (loadVm, loadCanvas) = CreateSetup();
            await LoadFromFile(loadVm, tempFile);

            loadCanvas.Components.First(c => c.Component.Identifier == "mmi_a")
                .Component.GetSlider(1)!.Value.ShouldBe(10, 1e-9);
            loadCanvas.Components.First(c => c.Component.Identifier == "mmi_b")
                .Component.GetSlider(1)!.Value.ShouldBe(90, 1e-9);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task OldFileWithoutSliderValues_FallsBackToLegacySingleValue()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"multislider_{Guid.NewGuid():N}.lun");
        try
        {
            var (saveVm, saveCanvas) = CreateSetup();
            var template = _library.First(t => t.Name == "Phase Shifter");
            var component = ComponentTemplates.CreateFromTemplate(template, 0, 0);
            component.Identifier = "ps_legacy";
            saveCanvas.AddComponent(component, template.Name);
            component.GetSlider(0)!.Value = 123;

            await SaveToFile(saveVm, tempFile);

            // Simulate an old file (before multi-slider persistence): strip the multi-slider map, keep SliderValue.
            var json = await File.ReadAllTextAsync(tempFile);
            json = json.Replace("\"SliderValues\"", "\"SliderValuesLegacyRemoved\"")
                       .Replace("\"sliderValues\"", "\"sliderValuesLegacyRemoved\"");
            await File.WriteAllTextAsync(tempFile, json);

            var (loadVm, loadCanvas) = CreateSetup();
            await LoadFromFile(loadVm, tempFile);

            loadCanvas.Components.First(c => c.Component.Identifier == "ps_legacy")
                .Component.GetSlider(0)!.Value.ShouldBe(123, 1e-9,
                    "old files must restore slider 0 from the legacy field");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // ── Helpers (same harness as AllComponentsRoundtripTests) ────────────────

    private (FileOperationsViewModel vm, DesignCanvasViewModel canvas) CreateSetup()
    {
        var canvas = new DesignCanvasViewModel();
        var vm = new FileOperationsViewModel(
            canvas,
            new CommandManager(),
            new SimpleNazcaExporter(),
            new CAP_Core.Export.SaxExporter(),
            _library,
            new GdsExportViewModel(new CAP_Core.Export.GdsExportService()),
            new PhotonTorchExportViewModel(new CAP_Core.Export.PhotonTorchExporter(), canvas),
            null!);
        return (vm, canvas);
    }

    private static async Task SaveToFile(FileOperationsViewModel vm, string filePath)
    {
        var dialog = new Mock<IFileDialogService>();
        dialog.Setup(f => f.ShowSaveFileDialogAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(filePath);
        vm.FileDialogService = dialog.Object;
        await vm.SaveDesignAsCommand.ExecuteAsync(null);
        File.Exists(filePath).ShouldBeTrue("Design file must be created during save");
    }

    private static async Task LoadFromFile(FileOperationsViewModel vm, string filePath)
    {
        var dialog = new Mock<IFileDialogService>();
        dialog.Setup(f => f.ShowOpenFileDialogAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(filePath);
        vm.FileDialogService = dialog.Object;
        await vm.LoadDesignCommand.ExecuteAsync(null);
    }
}
