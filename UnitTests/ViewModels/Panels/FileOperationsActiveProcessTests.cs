using System.Collections.ObjectModel;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Export;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP_Core.Components.Process;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.ViewModels.Panels;

/// <summary>
/// Verifies that <see cref="FileOperationsViewModel"/> tracks, persists, and migrates the
/// active process selection (issue #570): save/load round-trips an explicit selection, and
/// a legacy file (no stored ActiveProcess) infers one from its placed components' PDKs.
/// </summary>
public class FileOperationsActiveProcessTests
{
    private readonly ObservableCollection<ComponentTemplate> _library;

    public FileOperationsActiveProcessTests()
    {
        _library = new ObservableCollection<ComponentTemplate>(TestPdkLoader.LoadAllTemplates());
    }

    [Fact]
    public async Task SetActiveProcess_ThenSaveLoad_RestoresSelection()
    {
        var (saveVm, _) = CreateFileOperationsSetup();
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_active_process_{Guid.NewGuid()}.lun");

        try
        {
            saveVm.SetActiveProcess(ActiveProcessSelection.Playground());
            saveVm.HasUnsavedChanges.ShouldBeTrue();

            var mockDialog = new Mock<IFileDialogService>();
            mockDialog.Setup(f => f.ShowSaveFileDialogAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(tempFile);
            saveVm.FileDialogService = mockDialog.Object;
            await saveVm.SaveDesignAsCommand.ExecuteAsync(null);

            var (loadVm, _) = CreateFileOperationsSetup();
            var loadDialog = new Mock<IFileDialogService>();
            loadDialog.Setup(f => f.ShowOpenFileDialogAsync(
                It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(tempFile);
            loadVm.FileDialogService = loadDialog.Object;
            await loadVm.LoadDesignCommand.ExecuteAsync(null);

            loadVm.ActiveProcess.ShouldNotBeNull();
            loadVm.ActiveProcess!.IsPlayground.ShouldBeTrue();
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task LoadDesign_LegacyFileWithoutActiveProcess_MigratesFromComponentPdkSources()
    {
        var (loadVm, loadCanvas) = CreateFileOperationsSetup();
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_legacy_process_{Guid.NewGuid()}.lun");

        var detectorTemplate = _library.First(t => t.Name == "Photodetector");
        var pdkSource = detectorTemplate.PdkSource;
        var soiGroup = new ProcessGroup(
            "SOI Process",
            new ProcessFingerprint("Si", 220, "SiO2", 1550, "SOI Process"),
            new[] { pdkSource });

        try
        {
            // v2.0 file with no ActiveProcess section, but a placed component whose
            // PdkSource matches exactly one group in the provided catalog.
            var legacyData = new
            {
                FormatVersion = "2.0",
                Components = new[]
                {
                    new
                    {
                        TemplateName = detectorTemplate.Name,
                        PdkSource = pdkSource,
                        X = 0.0,
                        Y = 0.0,
                        Identifier = "legacy_det_1",
                        Rotation = 0
                    }
                },
                Connections = Array.Empty<object>()
            };

            var json = System.Text.Json.JsonSerializer.Serialize(legacyData,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(tempFile, json);

            loadVm.ProcessCatalogProvider = () => new[] { soiGroup };
            string? migrationWarning = null;
            loadVm.OnProcessMigrationWarning = w => migrationWarning = w;

            var loadDialog = new Mock<IFileDialogService>();
            loadDialog.Setup(f => f.ShowOpenFileDialogAsync(
                It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(tempFile);
            loadVm.FileDialogService = loadDialog.Object;
            await loadVm.LoadDesignCommand.ExecuteAsync(null);

            loadCanvas.Components.Count.ShouldBe(1);
            loadVm.ActiveProcess.ShouldNotBeNull();
            loadVm.ActiveProcess!.IsPlayground.ShouldBeFalse();
            loadVm.ActiveProcess.DisplayName.ShouldBe("SOI Process");
            migrationWarning.ShouldBeNull();
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    /// <summary>
    /// Creates a FileOperationsViewModel with a real component library for testing.
    /// Mirrors the setup helper in <c>DesignFileGroupPersistenceTests</c>.
    /// </summary>
    private (FileOperationsViewModel vm, DesignCanvasViewModel canvas) CreateFileOperationsSetup(
        CAP_Core.ErrorConsoleService? errorConsole = null)
    {
        var canvas = new DesignCanvasViewModel();
        var commandManager = new CommandManager();
        var nazcaExporter = new SimpleNazcaExporter();
        var gdsExport = new GdsExportViewModel(new CAP_Core.Export.GdsExportService());
        var photonTorchExport = new PhotonTorchExportViewModel(
            new CAP_Core.Export.PhotonTorchExporter(), canvas);

        var vm = new FileOperationsViewModel(
            canvas, commandManager, nazcaExporter, new CAP_Core.Export.SaxExporter(), _library,
            gdsExport, photonTorchExport, null!, errorConsole);

        return (vm, canvas);
    }
}
