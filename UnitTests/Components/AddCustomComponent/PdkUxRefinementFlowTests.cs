using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Components.AddCustomComponent;
using CAP_Core.Export;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

/// <summary>
/// End-to-end coverage for the PDK-/Prozess-UX-Feinschliff (issue #729 follow-up), one stage per
/// task: (a) <see cref="NewComponentViewModel.Save"/> without a prior explicit
/// <see cref="NewComponentViewModel.RunPreview"/> click still renders and persists (task 1);
/// (b) <see cref="CreateCustomPdkViewModel.SelectedTemplate"/> prefills the editor and
/// <see cref="CreateCustomPdkViewModel.CreatePdk"/> persists the user's edit on top of the
/// template, leaving the template itself untouched (task 2); (c)
/// <see cref="ProcessManagementViewModel.LoadForSinglePdkEdit"/> +
/// <see cref="ProcessManagementViewModel.SaveProcess"/> writes an edited cross-section width
/// back to the PDK's own file and fires <see cref="ProcessManagementViewModel.ProcessSaved"/>
/// (task 3). Each stage asserts exactly the one behavior task 4's brief calls out for it.
/// </summary>
public class PdkUxRefinementFlowTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lunima-pdk-ux-refinement-e2e-" + Guid.NewGuid().ToString("N"));

    private static PdkComponentDraft SeedComponent(string n) => new()
    {
        Name = n, WidthMicrometers = 5, HeightMicrometers = 1,
        RawCode = "import gdsfactory as gf\ncomponent = gf.components.straight()", RawCodeBackend = "gdsfactory",
        Pins = new() { new PhysicalPinDraft { Name = "o1" }, new PhysicalPinDraft { Name = "o2" } }
    };

    // (a) Save without a preceding Preview click: the extractor mock succeeds, so Save renders
    // the code itself (via the private EnsurePreviewAsync helper) and persists the result.
    [Fact]
    public async Task Task1_Save_withoutAPriorPreviewClick_persistsTheComponent()
    {
        var nazca = new Mock<IComponentPreviewRenderer>();
        var gds = new Mock<IComponentPreviewRenderer>();
        gds.Setup(g => g.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new NazcaPreviewResult
        {
            Success = true, XMin = 0, YMin = 0, XMax = 10, YMax = 2,
            Polygons = new List<NazcaPreviewPolygon>
            {
                new() { Layer = 1, Vertices = new List<(double, double)> { (0, 0), (10, 0), (10, 2), (0, 2) } }
            },
            Pins = new List<NazcaPreviewPin> { new() { Name = "o1", X = 0, Y = 1, Angle = 180 }, new() { Name = "o2", X = 10, Y = 1, Angle = 0 } }
        });
        var extractor = new ComponentGeometryExtractor(nazca.Object, gds.Object);
        var store = new UserPdkStore(_root, new PdkJsonSaver(), new PdkLoader());
        var process = new ProcessDefinition { Name = "P" };
        store.SaveToNamedPdk("My PDK", process, SeedComponent("seed"), "gdsfactory", null);
        var vm = new NewComponentViewModel(extractor, fdtd: null, store, new List<ProcessDefinition> { process })
        {
            ComponentName = "My Comp",
            SelectedBackend = GeometryBackend.GdsFactory,
            Code = "import gdsfactory as gf\ncomponent = gf.components.coupler()"
        };

        vm.HasPreview.ShouldBeFalse(); // no Preview click happened
        await vm.SaveCommand.ExecuteAsync(null);

        vm.SavedDraft.ShouldNotBeNull();
        var reloaded = new PdkLoader().LoadFromFileForEditing(vm.SavedFilePath!);
        reloaded.Components.ShouldContain(c => c.Name == "My Comp");
    }

    // (b) Picking a template prefills the editor; the user modifies a cross-section width before
    // CreatePdk, and only the modified value is persisted — the template itself is untouched.
    [Fact]
    public void Task2_TemplateSelection_PrefillsEditor_AndCreatePdk_PersistsTheModifiedWidth()
    {
        var store = new UserPdkStore(_root, new PdkJsonSaver(), new PdkLoader());
        var template = new ProcessDefinition
        {
            Name = "CornerStone SiN 300",
            CoreThicknessNm = 310,
            Xsections = new List<ProcessXsection>
            {
                new() { Name = "strip", Kind = XsectionKind.Optical, WidthUm = 0.5, MinRadiusUm = 5, RecommendedRadiusUm = 10 },
            },
        };
        var vm = new CreateCustomPdkViewModel(store, new[] { template }, new ProcessManagementViewModel(Mock.Of<IFileDialogService>()));

        vm.SelectedTemplate = template;
        vm.ProcessDefinitionEditor.Xsections.ShouldContain(x => x.Name == "strip" && x.WidthUm == 0.5);

        vm.ProcessDefinitionEditor.Xsections.Single(x => x.Name == "strip").WidthUm = 0.9;
        vm.PdkName = "My Template Lib";
        vm.ProcessSource = PdkProcessSource.DefineNew;
        vm.CreatePdkCommand.Execute(null);

        var reloaded = new PdkLoader().LoadFromFileForEditing(vm.CreatedFilePath!);
        reloaded.Process!.Xsections.Single(x => x.Name == "strip").WidthUm.ShouldBe(0.9);
        template.Xsections.Single(x => x.Name == "strip").WidthUm.ShouldBe(0.5,
            "the original template must stay a deep copy source, never mutated by the editor");
    }

    // (c) LoadForSinglePdkEdit scopes the editor to one PDK's own process; editing a
    // cross-section width and saving (resolver -> temp file, confirm true) writes it back to that
    // PDK's file and fires ProcessSaved.
    [Fact]
    public async Task Task3_LoadForSinglePdkEdit_ThenSaveProcess_WritesTheModifiedWidthToThePdkFile()
    {
        var dir = Path.Combine(_root, "single-pdk-edit");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "custom.json");
        var draft = new PdkDraft
        {
            Name = "MyCustomPdk",
            Process = new ProcessDefinition
            {
                Name = "MyCustomPdk",
                Xsections = new List<ProcessXsection> { new() { Name = "strip", WidthUm = 0.5 } },
            },
        };
        new PdkJsonSaver().SaveToFile(draft, path);

        var vm = new ProcessManagementViewModel(Mock.Of<IFileDialogService>())
        {
            PdkFilePathResolver = name => name == "MyCustomPdk" ? path : null,
            ConfirmSaveToPdk = _ => Task.FromResult(true),
        };
        vm.LoadForSinglePdkEdit(draft);
        var savedRaised = false;
        vm.ProcessSaved += (_, _) => savedRaised = true;

        vm.Xsections.Single(x => x.Name == "strip").WidthUm = 0.9;
        await vm.SaveProcessCommand.ExecuteAsync(null);

        savedRaised.ShouldBeTrue();
        var reloaded = new PdkLoader().LoadFromFileForEditing(path);
        reloaded.Process!.Xsections.Single(x => x.Name == "strip").WidthUm.ShouldBe(0.9);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
