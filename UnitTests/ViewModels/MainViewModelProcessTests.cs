using CAP_Core.Components.Process;
using Shouldly;
using UnitTests.Helpers;

namespace UnitTests.ViewModels;

/// <summary>
/// Unit tests for the New-Design process-selection wiring and active-process indicator
/// on <see cref="MainViewModel"/> (issue #570, Task 9). Covers only observable VM state —
/// the actual dialog (<c>ShowProcessSelectionAsync</c>) is left null so <c>NewProject</c>
/// takes the headless fallback path and never blocks on a UI callback.
/// </summary>
public class MainViewModelProcessTests
{
    [Fact]
    public void ProcessCatalogProvider_IsWired_AndReturnsAtLeastOneGroup()
    {
        var vm = MainViewModelTestHelper.CreateMainViewModel();

        var provider = vm.FileOperations.ProcessCatalogProvider;

        provider.ShouldNotBeNull();
        var groups = provider!.Invoke();
        groups.Count.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void ActiveProcessLabel_DefaultsToNoProcessSelected()
    {
        var vm = MainViewModelTestHelper.CreateMainViewModel();

        vm.ActiveProcessLabel.ShouldBe("No process selected");
        vm.IsPlayground.ShouldBeFalse();
    }

    [Fact]
    public void SetActiveProcess_Playground_UpdatesIndicatorToWarnAndNotManufacturable()
    {
        var vm = MainViewModelTestHelper.CreateMainViewModel();

        vm.FileOperations.SetActiveProcess(ActiveProcessSelection.Playground());

        vm.IsPlayground.ShouldBeTrue();
        vm.ActiveProcessLabel.ShouldContain("Playground");
        vm.ActiveProcessLabel.ShouldContain("not manufacturable");
    }

    [Fact]
    public void SetActiveProcess_ForGroup_UpdatesIndicatorToProcessName()
    {
        var vm = MainViewModelTestHelper.CreateMainViewModel();
        var groups = vm.FileOperations.ProcessCatalogProvider!.Invoke();
        var group = groups[0];

        vm.FileOperations.SetActiveProcess(ActiveProcessSelection.ForGroup(group));

        vm.IsPlayground.ShouldBeFalse();
        vm.ActiveProcessLabel.ShouldBe($"Process: {group.DisplayName}");
    }

    [Fact]
    public void SetActiveProcess_Null_RestoresNoProcessSelected()
    {
        var vm = MainViewModelTestHelper.CreateMainViewModel();
        vm.FileOperations.SetActiveProcess(ActiveProcessSelection.Playground());

        vm.FileOperations.SetActiveProcess(null);

        vm.IsPlayground.ShouldBeFalse();
        vm.ActiveProcessLabel.ShouldBe("No process selected");
    }

    [Fact]
    public async Task NewProject_WithNoDialogWired_ProceedsWithoutSettingAProcess()
    {
        var vm = MainViewModelTestHelper.CreateMainViewModel();
        vm.ShowProcessSelectionAsync = null; // headless/test contexts leave this unset

        await vm.NewProjectCommand.ExecuteAsync(null);

        vm.FileOperations.ActiveProcess.ShouldBeNull();
        vm.ActiveProcessLabel.ShouldBe("No process selected");
    }

    [Fact]
    public async Task NewProject_WhenPickerReturnsNull_AbortsWithoutClearingCanvas()
    {
        var vm = MainViewModelTestHelper.CreateMainViewModel();
        vm.FileOperations.SetActiveProcess(ActiveProcessSelection.Playground());
        vm.ShowProcessSelectionAsync = _ => Task.FromResult<ActiveProcessSelection?>(null);

        await vm.NewProjectCommand.ExecuteAsync(null);

        // Cancelling the picker must abort New Design entirely — the previously
        // active process (and by extension the canvas) is left untouched.
        vm.FileOperations.ActiveProcess.ShouldNotBeNull();
        vm.IsPlayground.ShouldBeTrue();
    }

    [Fact]
    public async Task NewProject_WhenPickerConfirmsPlayground_SetsActiveProcessToPlayground()
    {
        var vm = MainViewModelTestHelper.CreateMainViewModel();
        vm.ShowProcessSelectionAsync = _ => Task.FromResult<ActiveProcessSelection?>(ActiveProcessSelection.Playground());

        await vm.NewProjectCommand.ExecuteAsync(null);

        vm.IsPlayground.ShouldBeTrue();
        vm.ActiveProcessLabel.ShouldContain("Playground");
    }
}
