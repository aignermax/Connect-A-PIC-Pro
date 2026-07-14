using System;
using System.Collections.Generic;
using System.IO;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Components.AddCustomComponent;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

/// <summary>
/// Verifies <see cref="CreateCustomPdkViewModel"/>: a name plus either an adopted existing
/// process or a freshly defined one creates a named user PDK via <see cref="UserPdkStore"/>,
/// with name-collision and empty-selection guards on <c>CreatePdk</c>'s availability.
/// </summary>
public class CreateCustomPdkViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lunima-createpdk-" + Guid.NewGuid().ToString("N"));

    private UserPdkStore CreateStore() => new(_root, new PdkJsonSaver(), new PdkLoader());

    private static ProcessDefinition ExistingProcess() => new()
    {
        Name = "CornerStone SiN 300",
        Materials = { new ProcessMaterial { Name = "Si3N4", Role = "core" } },
    };

    private CreateCustomPdkViewModel CreateVm(UserPdkStore store, IReadOnlyList<ProcessDefinition>? processes = null) =>
        new(store, processes ?? new[] { ExistingProcess() }, new ProcessManagementViewModel(Mock.Of<IFileDialogService>()));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void UseExisting_CreatePdk_WritesNamedPdkWithSelectedProcess_AndRaisesEvent()
    {
        var store = CreateStore();
        var vm = CreateVm(store);
        vm.PdkName = "My Custom Lib";
        vm.SelectedExistingProcess = vm.AvailableProcesses[0];

        string? raisedPath = null;
        vm.PdkCreated += (_, path) => raisedPath = path;

        vm.CreatePdkCommand.Execute(null);

        vm.CreatedFilePath.ShouldNotBeNull();
        raisedPath.ShouldBe(vm.CreatedFilePath);
        var listed = store.ListCustomPdks();
        listed.ShouldContain(p => p.Name == "My Custom Lib" && p.Process.Name == "CornerStone SiN 300");
    }

    [Fact]
    public void DefineNew_CreatePdk_UsesProcessDefinitionEditorToProcess()
    {
        var store = CreateStore();
        var vm = CreateVm(store);
        vm.PdkName = "Fresh Lib";
        vm.ProcessSource = PdkProcessSource.DefineNew;
        vm.ProcessDefinitionEditor.ProcessName = "My New Process";
        vm.ProcessDefinitionEditor.Materials.Add(new ProcessMaterial { Name = "Si", Role = "core" });

        vm.CreatePdkCommand.Execute(null);

        vm.CreatedFilePath.ShouldNotBeNull();
        var listed = store.ListCustomPdks();
        listed.ShouldContain(p => p.Name == "Fresh Lib" && p.Process.Name == "My New Process");
    }

    [Fact]
    public void CanCreate_IsFalse_WhenNameEmpty_OrUseExistingWithoutSelection()
    {
        var store = CreateStore();
        var vm = CreateVm(store);

        vm.CreatePdkCommand.CanExecute(null).ShouldBeFalse("no name yet");

        vm.PdkName = "Something";
        vm.CreatePdkCommand.CanExecute(null).ShouldBeFalse("UseExisting but nothing selected");

        vm.SelectedExistingProcess = vm.AvailableProcesses[0];
        vm.CreatePdkCommand.CanExecute(null).ShouldBeTrue();

        vm.ProcessSource = PdkProcessSource.DefineNew;
        vm.CreatePdkCommand.CanExecute(null).ShouldBeTrue("DefineNew doesn't require a selected existing process");
    }

    [Fact]
    public void CreatePdk_Collision_DoesNotCreate_AndSetsStatusText()
    {
        var store = CreateStore();
        store.CreateNamedPdkWithProcess("Taken", ExistingProcess(), "gdsfactory", null);

        var vm = CreateVm(store);
        vm.PdkName = "Taken";
        vm.SelectedExistingProcess = vm.AvailableProcesses[0];

        var eventFired = false;
        vm.PdkCreated += (_, _) => eventFired = true;

        vm.CreatePdkCommand.Execute(null);

        eventFired.ShouldBeFalse();
        vm.CreatedFilePath.ShouldBeNull();
        vm.StatusText.ShouldContain("already exists");
    }
}
