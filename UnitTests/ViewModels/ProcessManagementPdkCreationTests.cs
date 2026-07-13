using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.ViewModels;

/// <summary>
/// Verifies the PDK-creation mode of <see cref="ProcessManagementViewModel"/>: entering the
/// mode starts a fresh process, and <c>CreatePdkCommand</c> hands the name + edited process to
/// the caller-supplied <see cref="ProcessManagementViewModel.CreateUserPdk"/> callback and raises
/// <see cref="ProcessManagementViewModel.PdkCreated"/> with the resulting path. This mode must
/// never touch <c>ActiveProcess</c>/<c>FileOperationsViewModel</c> (out of scope, bug #726).
/// </summary>
public class ProcessManagementPdkCreationTests
{
    private static ProcessManagementViewModel Vm()
        => new(Mock.Of<IFileDialogService>());

    [Fact]
    public void CreatePdk_invokes_callback_with_name_and_process_and_raises_event()
    {
        var vm = Vm();
        vm.EnterPdkCreationMode();
        vm.PdkName = "My Lib";
        string? gotName = null;
        ProcessDefinition? gotProc = null;
        string? raised = null;
        vm.CreateUserPdk = (n, p) => { gotName = n; gotProc = p; return "C:/tmp/my-lib.json"; };
        vm.PdkCreated += (_, path) => raised = path;

        vm.CreatePdkCommand.Execute(null);

        gotName.ShouldBe("My Lib");
        gotProc.ShouldNotBeNull();
        raised.ShouldBe("C:/tmp/my-lib.json");
    }

    [Fact]
    public void CanCreatePdk_false_when_name_blank()
    {
        var vm = Vm();
        vm.EnterPdkCreationMode();
        vm.CreateUserPdk = (_, _) => "x";
        vm.PdkName = "   ";

        vm.CreatePdkCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public void CanCreatePdk_false_when_not_in_creation_mode()
    {
        var vm = Vm();
        vm.PdkName = "My Lib";
        vm.CreateUserPdk = (_, _) => "x";

        vm.CreatePdkCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public void CanCreatePdk_false_when_no_callback_wired()
    {
        var vm = Vm();
        vm.EnterPdkCreationMode();
        vm.PdkName = "My Lib";

        vm.CreatePdkCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public void EnterPdkCreationMode_starts_with_a_fresh_process()
    {
        var vm = Vm();

        vm.EnterPdkCreationMode();

        vm.IsPdkCreationMode.ShouldBeTrue();
        vm.HasProcess.ShouldBeTrue();
        vm.ProcessName.ShouldBe("New process");
    }

    [Fact]
    public void CreatePdk_on_name_collision_sets_status_and_does_not_invoke_callback()
    {
        var vm = Vm();
        vm.EnterPdkCreationMode();
        vm.PdkName = "Existing";
        var invoked = false;
        vm.CreateUserPdk = (_, _) => { invoked = true; return "should-not-happen"; };
        vm.PdkNameExists = _ => true;
        string? raised = null;
        vm.PdkCreated += (_, path) => raised = path;

        vm.CreatePdkCommand.Execute(null);

        invoked.ShouldBeFalse();
        raised.ShouldBeNull();
        vm.StatusText.ShouldContain("Existing");
    }
}
