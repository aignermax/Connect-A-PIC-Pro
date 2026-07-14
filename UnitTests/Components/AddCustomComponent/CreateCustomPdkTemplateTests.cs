using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// Verifies the "Start from template" prefill on <see cref="CreateCustomPdkViewModel"/>
/// (task 2 of the PDK-process UX refinement): picking <see cref="CreateCustomPdkViewModel.SelectedTemplate"/>
/// loads that process into <see cref="CreateCustomPdkViewModel.ProcessDefinitionEditor"/> as an editable
/// starting point (layer stack, cross-sections, materials, name, core thickness) — the user can then
/// modify it freely before <c>CreatePdk</c> persists whatever ends up in the editor, not the original
/// template values.
/// </summary>
public class CreateCustomPdkTemplateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lunima-createpdk-template-" + Guid.NewGuid().ToString("N"));

    private UserPdkStore CreateStore() => new(_root, new PdkJsonSaver(), new PdkLoader());

    private static ProcessDefinition TemplateProcess() => new()
    {
        Name = "CornerStone SiN 300",
        CoreThicknessNm = 310,
        Layers = new List<ProcessLayer>
        {
            new() { Name = "WAVEGUIDE", Layer = 1, Datatype = 0 },
        },
        Xsections = new List<ProcessXsection>
        {
            new() { Name = "strip", Kind = XsectionKind.Optical, WidthUm = 0.5, MinRadiusUm = 5, RecommendedRadiusUm = 10 },
        },
        Materials = new List<ProcessMaterial>
        {
            new() { Name = "Si3N4", Role = "core" },
            new() { Name = "SiO2", Role = "cladding" },
        },
    };

    private CreateCustomPdkViewModel CreateVm(UserPdkStore store, ProcessDefinition template) =>
        new(store, new[] { template }, new ProcessManagementViewModel(Mock.Of<IFileDialogService>()));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void SelectingTemplate_PrefillsEditor_NameLayersXsectionsMaterialsAndCoreThickness()
    {
        var store = CreateStore();
        var template = TemplateProcess();
        var vm = CreateVm(store, template);

        vm.SelectedTemplate = template;

        vm.ProcessDefinitionEditor.ProcessName.ShouldBe("CornerStone SiN 300");
        vm.ProcessDefinitionEditor.Layers.ShouldContain(l => l.Name == "WAVEGUIDE");
        vm.ProcessDefinitionEditor.Xsections.ShouldContain(x => x.Name == "strip" && x.WidthUm == 0.5);
        vm.ProcessDefinitionEditor.Materials.ShouldContain(m => m.Name == "Si3N4");
        vm.CoreThicknessNm.ShouldBe(310);
    }

    [Fact]
    public void AfterTemplatePrefill_ModifiedValues_ArePersisted_NotTheOriginalTemplate()
    {
        var store = CreateStore();
        var template = TemplateProcess();
        var vm = CreateVm(store, template);

        vm.SelectedTemplate = template;
        // Modify the prefilled cross-section width — the user is free to diverge from the template.
        vm.ProcessDefinitionEditor.Xsections.Single(x => x.Name == "strip").WidthUm = 0.9;
        vm.PdkName = "My Template Lib";
        vm.ProcessSource = PdkProcessSource.DefineNew;

        vm.CreatePdkCommand.Execute(null);

        vm.CreatedFilePath.ShouldNotBeNull();
        var reloaded = new PdkLoader().LoadFromFileForEditing(vm.CreatedFilePath!);
        reloaded.Process!.Xsections.Single(x => x.Name == "strip").WidthUm.ShouldBe(0.9,
            "the saved process must reflect the user's edit on top of the template, not the template's original value");
        reloaded.Process.CoreThicknessNm.ShouldBe(310);

        // The template is a live process object (in the real app it can be the in-memory
        // ProcessDefinition of an already-loaded PDK) — editing the prefilled editor must never
        // mutate it, so the template stays a copy-on-select, not a shared reference.
        template.Xsections.Single(x => x.Name == "strip").WidthUm.ShouldBe(0.5,
            "editing the prefilled editor must not mutate the original template's process object");
    }

    [Fact]
    public void SelectedTemplate_SetToNull_DoesNotThrow_AndLeavesEditorUntouched()
    {
        var store = CreateStore();
        var template = TemplateProcess();
        var vm = CreateVm(store, template);

        vm.SelectedTemplate = template;
        var nameBeforeClear = vm.ProcessDefinitionEditor.ProcessName;

        Should.NotThrow(() => vm.SelectedTemplate = null);

        vm.ProcessDefinitionEditor.ProcessName.ShouldBe(nameBeforeClear);
    }

    /// <summary>
    /// Finding 2 (#733 review): <see cref="ProcessManagementViewModel.Load"/> only copied
    /// Name+Layers/Xsections/Materials, and <see cref="ProcessManagementViewModel.ToProcess"/>
    /// only built from those — so a template's <c>AllowedAngles</c>/
    /// <c>ElectricalBridgeRequired</c>/<c>Foundry</c>/<c>Version</c> silently vanished on
    /// CreatePdk even though the template prefill and the editor never touch them.
    /// </summary>
    [Fact]
    public void SelectingTemplate_ThenCreatePdk_PreservesAllowedAnglesAndElectricalBridgeRequired()
    {
        var store = CreateStore();
        var template = TemplateProcess();
        template.AllowedAngles = new List<int> { 0, 90, 180, 270 };
        template.ElectricalBridgeRequired = true;
        template.Foundry = "AcmeFab";
        template.Version = "v3";
        var vm = CreateVm(store, template);

        vm.SelectedTemplate = template;
        vm.PdkName = "My Angled Lib";
        vm.ProcessSource = PdkProcessSource.DefineNew;
        vm.CreatePdkCommand.Execute(null);

        var reloaded = new PdkLoader().LoadFromFileForEditing(vm.CreatedFilePath!);
        reloaded.Process!.AllowedAngles.ShouldBe(new List<int> { 0, 90, 180, 270 });
        reloaded.Process.ElectricalBridgeRequired.ShouldBe(true);
        reloaded.Process.Foundry.ShouldBe("AcmeFab");
        reloaded.Process.Version.ShouldBe("v3");
    }
}
