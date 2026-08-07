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

public class CreateCustomPdkViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lunima-createpdk-" + Guid.NewGuid().ToString("N"));

    /// <summary>StatusText assertions expect English substrings ("built-in"); pin the language
    /// so the tests don't depend on the machine locale or on which test class ran first
    /// (same pattern as the PdkOffset test classes, issue #749).</summary>
    public CreateCustomPdkViewModelTests() =>
        CAP.Avalonia.Services.Localization.LocalizationService.Instance.SetLanguage(
            CAP.Avalonia.Services.Localization.SupportedLanguage.English.Code);

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
            try { Directory.Delete(_root, true); } catch { }
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
        listed.ShouldContain(p => p.Name == "My Custom Lib" && p.Process!.Name == "CornerStone SiN 300");
    }

    [Fact]
    public void DefineNew_CreatePdk_UsesProcessDefinitionEditorToProcess_WithCoreThickness()
    {
        var store = CreateStore();
        var vm = CreateVm(store);
        vm.PdkName = "Fresh Lib";
        vm.ProcessSource = PdkProcessSource.DefineNew;
        vm.ProcessDefinitionEditor.ProcessName = "My New Process";
        vm.ProcessDefinitionEditor.AddXsectionCommand.Execute(null);
        vm.CoreThicknessNm = 220;

        vm.CreatePdkCommand.Execute(null);

        vm.CreatedFilePath.ShouldNotBeNull();
        var reloaded = new PdkLoader().LoadFromFileForEditing(vm.CreatedFilePath!);
        reloaded.Name.ShouldBe("Fresh Lib");
        reloaded.Process!.Name.ShouldBe("My New Process");
        reloaded.Process.CoreThicknessNm.ShouldBe(220);
    }

    [Fact]
    public void DefineNew_CreatedProcess_HasSpecifiedFingerprint()
    {
        var store = CreateStore();
        var vm = CreateVm(store);
        vm.PdkName = "Spec Lib";
        vm.ProcessSource = PdkProcessSource.DefineNew;
        vm.ProcessDefinitionEditor.AddXsectionCommand.Execute(null);
        vm.CoreThicknessNm = 220;

        vm.CreatePdkCommand.Execute(null);

        var reloaded = new PdkLoader().LoadFromFileForEditing(vm.CreatedFilePath!);
        ProcessFingerprintFactory.From(reloaded).IsSpecified.ShouldBeTrue(
            "a define-new process with core/cladding materials + core thickness must yield a specified fingerprint");
    }

    [Fact]
    public void CanCreate_IsFalse_WhenNameEmpty_OrUseExistingWithoutSelection_OrDefineNewWithoutContent()
    {
        var store = CreateStore();
        var vm = CreateVm(store);

        vm.CreatePdkCommand.CanExecute(null).ShouldBeFalse("no name yet");

        vm.PdkName = "Something";
        vm.CreatePdkCommand.CanExecute(null).ShouldBeFalse("UseExisting but nothing selected");

        vm.SelectedExistingProcess = vm.AvailableProcesses[0];
        vm.CreatePdkCommand.CanExecute(null).ShouldBeTrue();

        vm.ProcessSource = PdkProcessSource.DefineNew;
        vm.CreatePdkCommand.CanExecute(null).ShouldBeFalse("DefineNew with no cross-sections is an empty process");

        vm.ProcessDefinitionEditor.AddXsectionCommand.Execute(null);
        vm.CreatePdkCommand.CanExecute(null).ShouldBeTrue("DefineNew becomes valid once it has a cross-section");
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

    [Fact]
    public void CreatePdk_CollisionIsByDisplayName_CaseInsensitive()
    {
        var store = CreateStore();
        store.CreateNamedPdkWithProcess("My Lib", ExistingProcess(), "gdsfactory", null);

        var vm = CreateVm(store);
        vm.SelectedExistingProcess = vm.AvailableProcesses[0];
        vm.PdkName = "MY LIB";

        vm.CreatePdkCommand.Execute(null);

        vm.CreatedFilePath.ShouldBeNull();
        vm.StatusText.ShouldContain("already exists");
    }

    [Fact]
    public void CreatePdk_NameCollidingWithALoadedBundledPdk_IsRejectedWithAnExplanation()
    {
        // A brand-new user PDK named like a loaded bundled PDK
        // would be mistaken for its fork on the next save/startup and silently displace the
        // whole built-in library — block the name at creation time instead.
        var store = CreateStore();
        var vm = new CreateCustomPdkViewModel(
            store, new[] { ExistingProcess() },
            new ProcessManagementViewModel(Mock.Of<IFileDialogService>()),
            reservedBundledPdkNames: new[] { "CornerStone SiN" });
        vm.SelectedExistingProcess = vm.AvailableProcesses[0];
        vm.PdkName = "cornerstone sin"; // case-insensitive

        var eventFired = false;
        vm.PdkCreated += (_, _) => eventFired = true;

        vm.CreatePdkCommand.Execute(null);

        eventFired.ShouldBeFalse();
        vm.CreatedFilePath.ShouldBeNull();
        store.ListCustomPdks().ShouldBeEmpty("no file may be created under a bundled PDK's name");
        vm.StatusText.ShouldContain("built-in");
    }

    [Fact]
    public void CreatePdk_DoesNotThrow_WhenStoreLevelSlugCollisionOccurs()
    {
        var store = CreateStore();
        store.CreateNamedPdkWithProcess("My Lib", ExistingProcess(), "gdsfactory", null);

        var vm = CreateVm(store);
        vm.SelectedExistingProcess = vm.AvailableProcesses[0];
        vm.PdkName = "My  Lib";

        Should.NotThrow(() => vm.CreatePdkCommand.Execute(null));
        vm.CreatedFilePath.ShouldBeNull();
        vm.StatusText.ShouldNotBeNullOrEmpty();
    }
}
