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
/// Unit tests for the project-lifecycle surface of <see cref="FileOperationsViewModel"/>
/// added for the Home screen: path-based loading split from the file picker,
/// unsaved-changes prompts before load/close, recent-projects recording, the
/// observable <c>CurrentFilePath</c>, and the <c>ProjectOpened</c> callback.
/// </summary>
public class FileOperationsProjectLifecycleTests : IDisposable
{
    private readonly DesignCanvasViewModel _canvas;
    private readonly FileOperationsViewModel _fileOps;
    private readonly RecentProjectsService _recentProjects;
    private readonly Mock<IFileDialogService> _fileDialog;
    private readonly Mock<IMessageBoxService> _messageBox;
    private readonly string _testPreferencesPath;
    private readonly string _tempDesignPath;

    public FileOperationsProjectLifecycleTests()
    {
        _canvas = new DesignCanvasViewModel();
        _testPreferencesPath = Path.Combine(Path.GetTempPath(), $"test-lifecycle-prefs-{Guid.NewGuid()}.json");
        _tempDesignPath = Path.Combine(Path.GetTempPath(), $"test-lifecycle-design-{Guid.NewGuid():N}.lun");
        _recentProjects = new RecentProjectsService(new UserPreferencesService(_testPreferencesPath));

        var photonTorchVm = new PhotonTorchExportViewModel(new PhotonTorchExporter(), _canvas);
        _fileOps = new FileOperationsViewModel(
            _canvas,
            new CommandManager(),
            new SimpleNazcaExporter(),
            new SaxExporter(),
            new ObservableCollection<ComponentTemplate>(),
            new GdsExportViewModel(new GdsExportService()),
            photonTorchVm,
            null!,
            recentProjects: _recentProjects);

        _fileDialog = new Mock<IFileDialogService>();
        _messageBox = new Mock<IMessageBoxService>();
        _fileOps.FileDialogService = _fileDialog.Object;
        _fileOps.MessageBoxService = _messageBox.Object;
    }

    public void Dispose()
    {
        foreach (var path in new[] { _testPreferencesPath, _tempDesignPath })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private void SetupSavePrompt(SavePromptResult result)
    {
        _messageBox
            .Setup(m => m.ShowSavePromptAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(result);
    }

    /// <summary>Saves the current (empty) canvas to the temp design path via the mocked save dialog.</summary>
    private async Task SaveEmptyDesignToTempPathAsync()
    {
        _fileDialog
            .Setup(d => d.ShowSaveFileDialogAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(_tempDesignPath);
        await _fileOps.SaveDesignCommand.ExecuteAsync(null);
        File.Exists(_tempDesignPath).ShouldBeTrue("saving the seed design file must succeed");
    }

    [Fact]
    public async Task LoadDesignFromPathAsync_MissingFile_ReturnsFalse()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.lun");

        var loaded = await _fileOps.LoadDesignFromPathAsync(missing);

        loaded.ShouldBeFalse();
        _fileOps.CurrentFilePath.ShouldBeNull();
    }

    [Fact]
    public async Task LoadDesignFromPathAsync_ValidFile_LoadsAndSetsCurrentFilePath()
    {
        await SaveEmptyDesignToTempPathAsync();
        _fileOps.CurrentFilePath.ShouldBe(_tempDesignPath);

        await _fileOps.NewProjectCommand.ExecuteAsync(null);
        _fileOps.CurrentFilePath.ShouldBeNull();

        var loaded = await _fileOps.LoadDesignFromPathAsync(_tempDesignPath);

        loaded.ShouldBeTrue();
        _fileOps.CurrentFilePath.ShouldBe(_tempDesignPath);
        _fileOps.HasUnsavedChanges.ShouldBeFalse();
    }

    [Fact]
    public async Task LoadDesignFromPathAsync_UnsavedChangesAndCancel_ReturnsFalseWithoutLoading()
    {
        await SaveEmptyDesignToTempPathAsync();
        _fileOps.HasUnsavedChanges = true;
        SetupSavePrompt(SavePromptResult.Cancel);

        var loaded = await _fileOps.LoadDesignFromPathAsync(_tempDesignPath);

        loaded.ShouldBeFalse();
        _fileOps.HasUnsavedChanges.ShouldBeTrue();
    }

    [Fact]
    public async Task LoadDesignFromPathAsync_UnsavedChangesAndDontSave_Loads()
    {
        await SaveEmptyDesignToTempPathAsync();
        _fileOps.HasUnsavedChanges = true;
        SetupSavePrompt(SavePromptResult.DontSave);

        var loaded = await _fileOps.LoadDesignFromPathAsync(_tempDesignPath);

        loaded.ShouldBeTrue();
        _fileOps.HasUnsavedChanges.ShouldBeFalse();
    }

    [Fact]
    public async Task LoadDesignCommand_UnsavedChangesAndCancel_NeverOpensFilePicker()
    {
        _fileOps.HasUnsavedChanges = true;
        SetupSavePrompt(SavePromptResult.Cancel);

        await _fileOps.LoadDesignCommand.ExecuteAsync(null);

        _fileDialog.Verify(
            d => d.ShowOpenFileDialogAsync(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task SaveDesign_RecordsProjectInRecentList()
    {
        await SaveEmptyDesignToTempPathAsync();

        _recentProjects.GetRecentProjects().ShouldHaveSingleItem()
            .FilePath.ShouldBe(Path.GetFullPath(_tempDesignPath));
    }

    [Fact]
    public async Task LoadDesignFromPathAsync_RecordsProjectInRecentList()
    {
        await SaveEmptyDesignToTempPathAsync();
        _recentProjects.ClearAll();

        await _fileOps.LoadDesignFromPathAsync(_tempDesignPath);

        _recentProjects.GetRecentProjects().ShouldHaveSingleItem()
            .FilePath.ShouldBe(Path.GetFullPath(_tempDesignPath));
    }

    [Fact]
    public async Task CurrentFilePath_RaisesPropertyChangedOnLoad()
    {
        await SaveEmptyDesignToTempPathAsync();
        await _fileOps.NewProjectCommand.ExecuteAsync(null);

        var changedProperties = new List<string?>();
        _fileOps.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        await _fileOps.LoadDesignFromPathAsync(_tempDesignPath);

        changedProperties.ShouldContain(nameof(FileOperationsViewModel.CurrentFilePath));
    }

    [Fact]
    public async Task ProjectOpened_FiresOnSuccessfulLoadAndNewProject()
    {
        await SaveEmptyDesignToTempPathAsync();

        var openedCount = 0;
        _fileOps.ProjectOpened = () => openedCount++;

        await _fileOps.LoadDesignFromPathAsync(_tempDesignPath);
        openedCount.ShouldBe(1);

        await _fileOps.NewProjectCommand.ExecuteAsync(null);
        openedCount.ShouldBe(2);
    }

    [Fact]
    public async Task ProjectOpened_DoesNotFireWhenLoadFails()
    {
        var fired = false;
        _fileOps.ProjectOpened = () => fired = true;

        await _fileOps.LoadDesignFromPathAsync(
            Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.lun"));

        fired.ShouldBeFalse();
    }

    [Fact]
    public async Task ConfirmCloseAsync_NoUnsavedChanges_ReturnsTrue()
    {
        (await _fileOps.ConfirmCloseAsync()).ShouldBeTrue();
        _messageBox.Verify(
            m => m.ShowSavePromptAsync(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task ConfirmCloseAsync_UnsavedChangesAndDontSave_ReturnsTrue()
    {
        _fileOps.HasUnsavedChanges = true;
        SetupSavePrompt(SavePromptResult.DontSave);

        (await _fileOps.ConfirmCloseAsync()).ShouldBeTrue();
    }

    [Fact]
    public async Task ConfirmCloseAsync_UnsavedChangesAndCancel_ReturnsFalse()
    {
        _fileOps.HasUnsavedChanges = true;
        SetupSavePrompt(SavePromptResult.Cancel);

        (await _fileOps.ConfirmCloseAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task ConfirmCloseAsync_UnsavedChangesAndSave_SavesThenReturnsTrue()
    {
        _fileDialog
            .Setup(d => d.ShowSaveFileDialogAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(_tempDesignPath);
        _fileOps.HasUnsavedChanges = true;
        SetupSavePrompt(SavePromptResult.Save);

        (await _fileOps.ConfirmCloseAsync()).ShouldBeTrue();

        _fileOps.HasUnsavedChanges.ShouldBeFalse();
        File.Exists(_tempDesignPath).ShouldBeTrue();
    }
}
