using CAP.Avalonia.Services;
using CAP_Core.Components.Process;
using Moq;
using Shouldly;
using UnitTests;
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
    public void ProcessCatalogProvider_ExcludesProcessAgnosticToolPdks()
    {
        // Final-review finding for #570: the process-agnostic "Analysis Tools" PDK must
        // not appear as a selectable fabrication process in the New-Design catalog, while
        // the real SOI process built from the bundled Demo/SiEPIC PDKs still does.
        var vm = MainViewModelTestHelper.CreateMainViewModel();

        var groups = vm.FileOperations.ProcessCatalogProvider!.Invoke();

        groups.ShouldNotContain(g => g.DisplayName == "Analysis Tools");
        groups.ShouldContain(g => g.MemberPdkNames.Contains("Demo PDK"));
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
    public async Task NewProject_WhenPickerDismissed_FallsBackToPlayground()
    {
        var vm = MainViewModelTestHelper.CreateMainViewModel();
        // Start from a real process so the fall-back to Playground is an observable change.
        var group = vm.FileOperations.ProcessCatalogProvider!.Invoke()[0];
        vm.FileOperations.SetActiveProcess(ActiveProcessSelection.ForGroup(group));
        vm.ShowProcessSelectionAsync = _ => Task.FromResult<ActiveProcessSelection?>(null);

        await vm.NewProjectCommand.ExecuteAsync(null);

        // On a freshly-cleared design, dismissing the picker defaults to Playground rather
        // than leaving the process undefined — the design is always in a known state.
        vm.IsPlayground.ShouldBeTrue();
        vm.ActiveProcessLabel.ShouldContain("Playground");
    }

    [Fact]
    public async Task PromptForInitialProcess_WhenPickerConfirmsProcess_SetsIt()
    {
        var vm = MainViewModelTestHelper.CreateMainViewModel();
        var group = vm.FileOperations.ProcessCatalogProvider!.Invoke()[0];
        vm.ShowProcessSelectionAsync = _ =>
            Task.FromResult<ActiveProcessSelection?>(ActiveProcessSelection.ForGroup(group));

        await vm.PromptForInitialProcessAsync();

        vm.IsPlayground.ShouldBeFalse();
        vm.ActiveProcessLabel.ShouldBe($"Process: {group.DisplayName}");
    }

    [Fact]
    public async Task PromptForInitialProcess_WhenDismissed_StartsInPlayground()
    {
        var vm = MainViewModelTestHelper.CreateMainViewModel();
        vm.ShowProcessSelectionAsync = _ => Task.FromResult<ActiveProcessSelection?>(null);

        await vm.PromptForInitialProcessAsync();

        vm.IsPlayground.ShouldBeTrue();
    }

    [Fact]
    public async Task PromptForInitialProcess_WhenProcessAlreadyActive_DoesNotPrompt()
    {
        var vm = MainViewModelTestHelper.CreateMainViewModel();
        vm.FileOperations.SetActiveProcess(ActiveProcessSelection.Playground());
        var promptShown = false;
        vm.ShowProcessSelectionAsync = _ =>
        {
            promptShown = true;
            return Task.FromResult<ActiveProcessSelection?>(null);
        };

        await vm.PromptForInitialProcessAsync();

        promptShown.ShouldBeFalse();   // a process is already established → no startup prompt
    }

    [Fact]
    public async Task NewProject_WhenSaveNoOpsWithUnsavedChanges_DoesNotApplyPickedProcess()
    {
        // Reproduces the #570 review finding: FileOperationsViewModel.NewProject silently
        // no-ops (early return) when there are unsaved changes and the user cancels the
        // save prompt. If MainViewModel applied the newly picked process regardless, the
        // toolbar/metadata would claim the new process while the canvas still held the old
        // design — the exact data-integrity violation #570 exists to prevent.
        var vm = MainViewModelTestHelper.CreateMainViewModel();
        vm.FileOperations.SetActiveProcess(ActiveProcessSelection.Playground());

        var mockMessageBox = new Mock<IMessageBoxService>();
        mockMessageBox
            .Setup(m => m.ShowSavePromptAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(SavePromptResult.Cancel);
        vm.FileOperations.MessageBoxService = mockMessageBox.Object;

        var component = TestComponentFactory.CreateStraightWaveGuide();
        vm.Canvas.AddComponent(component, "TestTemplate");
        vm.FileOperations.HasUnsavedChanges.ShouldBeTrue();

        var groups = vm.FileOperations.ProcessCatalogProvider!.Invoke();
        var otherSelection = ActiveProcessSelection.ForGroup(groups[0]);
        vm.ShowProcessSelectionAsync = _ => Task.FromResult<ActiveProcessSelection?>(otherSelection);

        await vm.NewProjectCommand.ExecuteAsync(null);

        // Save prompt was cancelled -> NewProjectCommand no-op'd -> canvas still holds
        // the old design -> the picker must never have been shown, and the previously
        // active process (Playground) must remain untouched.
        vm.Canvas.Components.Count.ShouldBe(1);
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
