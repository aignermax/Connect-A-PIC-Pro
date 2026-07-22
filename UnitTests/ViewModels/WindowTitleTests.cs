using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels;
using Moq;
using Shouldly;
using UnitTests.Helpers;

namespace UnitTests.ViewModels;

/// <summary>
/// Unit tests for <see cref="MainViewModel.WindowTitle"/> — the window title
/// derived from the open file path and the unsaved-changes flag:
/// "Lunima" (fresh), "Untitled* — Lunima" (dirty, no file),
/// "name.lun — Lunima" (saved), "name.lun* — Lunima" (saved then edited).
/// </summary>
public class WindowTitleTests : IDisposable
{
    private readonly string _tempDesignPath;

    public WindowTitleTests()
    {
        _tempDesignPath = Path.Combine(Path.GetTempPath(), $"title-test-{Guid.NewGuid():N}.lun");
    }

    public void Dispose()
    {
        if (File.Exists(_tempDesignPath))
        {
            File.Delete(_tempDesignPath);
        }
    }

    private static MainViewModel CreateMainViewModel() =>
        MainViewModelTestHelper.CreateMainViewModel();

    private void WireSaveDialog(MainViewModel vm)
    {
        var dialog = new Mock<IFileDialogService>();
        dialog
            .Setup(d => d.ShowSaveFileDialogAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(_tempDesignPath);
        vm.FileOperations.FileDialogService = dialog.Object;
    }

    [Fact]
    public void FreshApp_TitleIsBareAppName()
    {
        CreateMainViewModel().WindowTitle.ShouldBe("Lunima");
    }

    [Fact]
    public void UnsavedChangesWithoutFile_TitleShowsUntitledWithDirtyMarker()
    {
        var vm = CreateMainViewModel();

        vm.FileOperations.HasUnsavedChanges = true;

        vm.WindowTitle.ShouldBe("Untitled* — Lunima");
    }

    [Fact]
    public async Task AfterSave_TitleShowsFileNameWithoutDirtyMarker()
    {
        var vm = CreateMainViewModel();
        WireSaveDialog(vm);

        await vm.FileOperations.SaveDesignCommand.ExecuteAsync(null);

        vm.WindowTitle.ShouldBe($"{Path.GetFileName(_tempDesignPath)} — Lunima");
    }

    [Fact]
    public async Task EditAfterSave_TitleShowsFileNameWithDirtyMarker()
    {
        var vm = CreateMainViewModel();
        WireSaveDialog(vm);
        await vm.FileOperations.SaveDesignCommand.ExecuteAsync(null);

        vm.FileOperations.HasUnsavedChanges = true;

        vm.WindowTitle.ShouldBe($"{Path.GetFileName(_tempDesignPath)}* — Lunima");
    }

    [Fact]
    public async Task NewProjectAfterSave_TitleReturnsToBareAppName()
    {
        var vm = CreateMainViewModel();
        WireSaveDialog(vm);
        await vm.FileOperations.SaveDesignCommand.ExecuteAsync(null);

        await vm.NewProjectCommand.ExecuteAsync(null);

        vm.WindowTitle.ShouldBe("Lunima");
    }

    [Fact]
    public async Task TitleChange_RaisesPropertyChanged()
    {
        var vm = CreateMainViewModel();
        WireSaveDialog(vm);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await vm.FileOperations.SaveDesignCommand.ExecuteAsync(null);

        changed.ShouldContain(nameof(MainViewModel.WindowTitle));
    }
}
