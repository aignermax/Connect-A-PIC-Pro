using System.Collections.ObjectModel;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Export;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Export;
using Moq;
using Shouldly;

namespace UnitTests.ViewModels;

/// <summary>
/// Unit tests for <see cref="FileOperationsViewModel.OpenDesignAsCopyAsync"/> —
/// opening a template/example design detached from its source file: the design
/// loads, but the file path stays null (Save prompts for a new location, the
/// source can't be overwritten), the design is marked unsaved, and the source
/// file is NOT recorded in the recent-projects list.
/// </summary>
public class FileOperationsOpenAsCopyTests : IDisposable
{
    private readonly DesignCanvasViewModel _canvas;
    private readonly FileOperationsViewModel _fileOps;
    private readonly RecentProjectsService _recentProjects;
    private readonly Mock<IFileDialogService> _fileDialog;
    private readonly string _testPreferencesPath;
    private readonly string _sourceDesignPath;

    public FileOperationsOpenAsCopyTests()
    {
        _canvas = new DesignCanvasViewModel();
        _testPreferencesPath = Path.Combine(Path.GetTempPath(), $"test-ascopy-prefs-{Guid.NewGuid()}.json");
        _sourceDesignPath = Path.Combine(Path.GetTempPath(), $"test-ascopy-source-{Guid.NewGuid():N}.lun");
        _recentProjects = new RecentProjectsService(new UserPreferencesService(_testPreferencesPath));

        _fileOps = new FileOperationsViewModel(
            _canvas,
            new CommandManager(),
            new SimpleNazcaExporter(),
            new SaxExporter(),
            new ObservableCollection<ComponentTemplate>(),
            new GdsExportViewModel(new GdsExportService()),
            new PhotonTorchExportViewModel(new PhotonTorchExporter(), _canvas),
            null!,
            recentProjects: _recentProjects);

        _fileDialog = new Mock<IFileDialogService>();
        _fileOps.FileDialogService = _fileDialog.Object;
    }

    public void Dispose()
    {
        foreach (var path in new[] { _testPreferencesPath, _sourceDesignPath })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>Creates the source .lun by saving the empty canvas, then resets state.</summary>
    private async Task CreateSourceDesignAsync()
    {
        _fileDialog
            .Setup(d => d.ShowSaveFileDialogAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(_sourceDesignPath);
        await _fileOps.SaveDesignCommand.ExecuteAsync(null);
        await _fileOps.NewProjectCommand.ExecuteAsync(null);
        _recentProjects.ClearAll();
    }

    [Fact]
    public async Task OpenAsCopy_LoadsButLeavesCurrentFilePathNull()
    {
        await CreateSourceDesignAsync();

        var opened = await _fileOps.OpenDesignAsCopyAsync(_sourceDesignPath);

        opened.ShouldBeTrue();
        _fileOps.CurrentFilePath.ShouldBeNull();
    }

    [Fact]
    public async Task OpenAsCopy_MarksDesignAsUnsaved()
    {
        await CreateSourceDesignAsync();

        await _fileOps.OpenDesignAsCopyAsync(_sourceDesignPath);

        _fileOps.HasUnsavedChanges.ShouldBeTrue();
    }

    [Fact]
    public async Task OpenAsCopy_DoesNotRecordSourceInRecents()
    {
        await CreateSourceDesignAsync();

        await _fileOps.OpenDesignAsCopyAsync(_sourceDesignPath);

        _recentProjects.GetRecentProjects().ShouldBeEmpty();
    }

    [Fact]
    public async Task OpenAsCopy_FiresProjectOpened()
    {
        await CreateSourceDesignAsync();
        var fired = false;
        _fileOps.ProjectOpened = () => fired = true;

        await _fileOps.OpenDesignAsCopyAsync(_sourceDesignPath);

        fired.ShouldBeTrue();
    }

    [Fact]
    public async Task OpenAsCopy_MissingFile_ReturnsFalse()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.lun");

        (await _fileOps.OpenDesignAsCopyAsync(missing)).ShouldBeFalse();
    }

    [Fact]
    public async Task OpenAsCopy_SavedCopy_DoesNotInheritSourceMetadata()
    {
        await CreateSourceDesignAsync();
        // Age the source's Created date so inheritance would be observable
        var sourceJson = await File.ReadAllTextAsync(_sourceDesignPath);
        sourceJson = sourceJson.Replace(DateTime.UtcNow.ToString("yyyy-MM-dd"), "2020-01-01");
        await File.WriteAllTextAsync(_sourceDesignPath, sourceJson);

        await _fileOps.OpenDesignAsCopyAsync(_sourceDesignPath);

        var copyPath = Path.Combine(Path.GetTempPath(), $"test-ascopy-copy-{Guid.NewGuid():N}.lun");
        _fileDialog
            .Setup(d => d.ShowSaveFileDialogAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(copyPath);
        try
        {
            await _fileOps.SaveDesignCommand.ExecuteAsync(null);

            var copyJson = await File.ReadAllTextAsync(copyPath);
            copyJson.ShouldNotContain("2020-01-01",
                customMessage: "a copy must get a fresh Created date, not the example's");
        }
        finally
        {
            if (File.Exists(copyPath))
            {
                File.Delete(copyPath);
            }
        }
    }
}
