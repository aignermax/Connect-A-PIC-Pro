using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Components.AddCustomComponent;
using CAP.Avalonia.ViewModels.Hierarchy;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Components.Creation;
using CAP_Core.Components.Process;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

public class CreateCustomPdkFlowTests : IDisposable
{
    private readonly string _testPrefsPath;
    private readonly string _userPdkRoot;
    private readonly LeftPanelViewModel _leftPanel;
    private readonly UserPdkStore _store;

    public CreateCustomPdkFlowTests()
    {
        _testPrefsPath = Path.Combine(Path.GetTempPath(), $"CreateCustomPdkFlowPrefs_{Guid.NewGuid():N}.json");
        _userPdkRoot = Path.Combine(Path.GetTempPath(), $"CreateCustomPdkFlowUserPdks_{Guid.NewGuid():N}");
        _store = new UserPdkStore(_userPdkRoot, new PdkJsonSaver(), new PdkLoader());

        var preferencesService = new UserPreferencesService(_testPrefsPath);
        var canvas = new DesignCanvasViewModel();
        var groupLibrary = new GroupLibraryManager();
        var pdkLoader = new PdkLoader();

        _leftPanel = new LeftPanelViewModel(canvas, groupLibrary, pdkLoader, preferencesService,
            new HierarchyPanelViewModel(canvas), new PdkManagerViewModel(),
            new ComponentLibraryViewModel(groupLibrary));
        _leftPanel.Initialize();
    }

    public void Dispose()
    {
        if (File.Exists(_testPrefsPath))
        {
            try { File.Delete(_testPrefsPath); } catch { }
        }
        if (Directory.Exists(_userPdkRoot))
        {
            try { Directory.Delete(_userPdkRoot, true); } catch { }
        }
    }

    private static CreateCustomPdkViewModel CreateVm(UserPdkStore store, ProcessDefinition process) =>
        new(store, new[] { process }, new ProcessManagementViewModel(Mock.Of<IFileDialogService>()));

    private static ProcessDefinition CompatibleProcess() => new()
    {
        Name = "MyLib Process",
        CoreThicknessNm = 222,
        Materials = new List<ProcessMaterial>
        {
            new() { Name = "Si", Role = "core" },
            new() { Name = "SiO2", Role = "cladding" },
        },
    };

    private static PdkComponentDraft SimpleComponent(string name) => new()
    {
        Name = name,
        Category = "Test",
        NazcaFunction = "test.straight",
        WidthMicrometers = 10,
        HeightMicrometers = 2,
        Pins = new List<PhysicalPinDraft>
        {
            new() { Name = "a0", OffsetXMicrometers = 0, OffsetYMicrometers = 1, AngleDegrees = 180 },
            new() { Name = "b0", OffsetXMicrometers = 10, OffsetYMicrometers = 1, AngleDegrees = 0 },
        },
    };

    private void ApplyDemoProcessLock()
    {
        var demoName = _leftPanel.PdkManager.LoadedPdks.First(p => p.Name.Contains("Demo", StringComparison.OrdinalIgnoreCase)).Name;
        var demoDraft = _leftPanel.GetLoadedPdkDrafts().First(d => d.Name == demoName);
        var demoFingerprint = ProcessFingerprintFactory.From(demoDraft);

        var active = new ActiveProcessSelection(
            DisplayName: "Demo Process",
            Fingerprint: demoFingerprint,
            MemberPdkNames: new List<string> { demoName },
            IsPlayground: false);

        _leftPanel.ApplyActiveProcess(active);
    }

    [Fact]
    public void UseExisting_CreatePdk_AddsPdkWithAdoptedProcess_ToListCustomPdks()
    {
        var process = CompatibleProcess();
        var vm = CreateVm(_store, process);
        vm.PdkName = "MyLib";
        vm.SelectedExistingProcess = vm.AvailableProcesses[0];

        vm.CreatePdkCommand.Execute(null);

        _store.ListCustomPdks().ShouldContain(p => p.Name == "MyLib" && p.Process!.Name == process.Name,
            "creating a PDK via the dialog with an adopted existing process must persist it with that process");
    }

    [Fact]
    public void ValueCompatiblePdk_CreatedViaDialog_BecomesEnabledAndVisible_AfterRegisterAndReapply()
    {
        ApplyDemoProcessLock();

        var process = CompatibleProcess();
        var vm = CreateVm(_store, process);
        vm.PdkName = "MyLib";
        vm.SelectedExistingProcess = vm.AvailableProcesses[0];
        vm.CreatePdkCommand.Execute(null);
        var createdPath = vm.CreatedFilePath;
        createdPath.ShouldNotBeNull("the dialog must have written the new PDK before it can gain a component");

        var component = SimpleComponent("MyLib Straight");
        _store.SaveToNamedPdk("MyLib", process, component, "nazca", null);
        _leftPanel.RegisterSavedCustomComponent(component, "MyLib", createdPath!);

        _leftPanel.PdkManager.LoadedPdks.Single(p => p.Name == "MyLib").IsEnabled.ShouldBeTrue(
            "a value-compatible custom PDK created via the Create-Custom-PDK dialog must become enabled/visible under the active process lock");
    }

    [Fact]
    public void DefineNew_WithCoreThickness_YieldsVisiblePdk_UnderCompatibleActiveProcess()
    {
        ApplyDemoProcessLock();

        var vm = CreateVm(_store, CompatibleProcess());
        vm.PdkName = "DefLib";
        vm.ProcessSource = PdkProcessSource.DefineNew;
        vm.ProcessDefinitionEditor.AddXsectionCommand.Execute(null);
        vm.CoreThicknessNm = 222;
        vm.CreatePdkCommand.Execute(null);
        var createdPath = vm.CreatedFilePath;
        createdPath.ShouldNotBeNull("a define-new process with a cross-section and thickness must be creatable");

        var component = SimpleComponent("DefLib Straight");
        _store.AppendToExistingPdk(createdPath!, component);
        _leftPanel.RegisterSavedCustomComponent(component, "DefLib", createdPath!);

        _leftPanel.PdkManager.LoadedPdks.Single(p => p.Name == "DefLib").IsEnabled.ShouldBeTrue(
            "a define-new PDK with core thickness set has a specified, value-compatible fingerprint and must be enabled under the active process lock");
    }

    [Fact]
    public void NazcaExample_DefinesComponentFunction()
    {
        BackendCodeExamples.Nazca.ShouldContain("def component()");
    }
}
