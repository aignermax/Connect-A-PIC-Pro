using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Components.AddCustomComponent;
using CAP.Avalonia.ViewModels.Hierarchy;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Export;
using CAP_Core.Components.Creation;
using CAP_Core.Components.Process;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

/// <summary>
/// Covers <see cref="LeftPanelViewModel.CanEditTemplate"/> and
/// <see cref="LeftPanelViewModel.EditCustomComponent"/> (issue #656 follow-up, task 6): the
/// library's "Edit…" action must only ever act on a template that belongs to a currently-loaded,
/// non-bundled (custom) PDK, and must open the "New Component" assistant prefilled via
/// <see cref="NewComponentViewModel.LoadForEdit"/> — never the window itself, which cannot be
/// opened headlessly.
/// </summary>
public class LibraryEditActionTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    /// <summary>Builds a <see cref="LeftPanelViewModel"/>, optionally with the "add custom component" collaborators wired.</summary>
    private LeftPanelViewModel CreateLeftPanelViewModel(UserPdkStore? userPdkStore = null)
    {
        var canvas = new DesignCanvasViewModel();
        var libraryManager = new GroupLibraryManager();
        var pdkLoader = new PdkLoader();
        var preferencesPath = Path.Combine(Path.GetTempPath(), $"test-preferences-{Guid.NewGuid()}.json");
        var preferencesService = new UserPreferencesService(preferencesPath);

        AddCustomComponentDependencies? deps = null;
        if (userPdkStore != null)
        {
            var nazca = new Mock<IComponentPreviewRenderer>();
            var gds = new Mock<IComponentPreviewRenderer>();
            var extractor = new ComponentGeometryExtractor(nazca.Object, gds.Object);
            deps = new AddCustomComponentDependencies(extractor, Fdtd: null, UserPdkStore: userPdkStore);
        }

        return new LeftPanelViewModel(
            canvas, libraryManager, pdkLoader, preferencesService,
            new HierarchyPanelViewModel(canvas),
            new PdkManagerViewModel(),
            new ComponentLibraryViewModel(libraryManager),
            addCustomComponentDeps: deps);
    }

    /// <summary>Creates a <see cref="UserPdkStore"/> rooted at a fresh temp directory, tracked for cleanup.</summary>
    private UserPdkStore CreateUserPdkStore()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lunima-lea-{Guid.NewGuid():N}");
        _tempDirs.Add(root);
        return new UserPdkStore(root, new PdkJsonSaver(), new PdkLoader());
    }

    /// <summary>Writes a real user-PDK file containing a sample component into <paramref name="store"/>, mirroring <c>LeftPanelNewComponentTests</c>.</summary>
    private static (string filePath, string pdkName, PdkComponentDraft draft) SaveUserPdk(UserPdkStore store, string processName)
    {
        var process = new ProcessDefinition { Name = processName };
        var draft = new PdkComponentDraft
        {
            Name = "My Coupler", Category = "Custom",
            RawCode = "import gdsfactory as gf\ncomponent = gf.components.coupler()",
            RawCodeBackend = "gdsfactory",
            WidthMicrometers = 10, HeightMicrometers = 2,
            Pins = new List<PhysicalPinDraft>
            {
                new() { Name = "o1", OffsetXMicrometers = 0, OffsetYMicrometers = 1, AngleDegrees = 180 },
                new() { Name = "o2", OffsetXMicrometers = 10, OffsetYMicrometers = 1, AngleDegrees = 0 },
            }
        };
        var path = store.Save(process, draft, "gdsfactory", null);
        var pdkName = new PdkLoader().LoadFromFileForEditing(path).Name;
        return (path, pdkName, draft);
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs.Where(Directory.Exists))
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void CanEditTemplate_true_forTemplateBelongingToALoadedCustomPdk()
    {
        var vm = CreateLeftPanelViewModel();
        vm.PdkManager.RegisterPdk("My Custom Pdk", "/tmp/x.json", isBundled: false, componentCount: 1);
        var template = new ComponentTemplate { Name = "X", PdkSource = "My Custom Pdk" };

        vm.CanEditTemplate(template).ShouldBeTrue();
    }

    [Fact]
    public void CanEditTemplate_false_forTemplateBelongingToABundledFoundryPdk()
    {
        var vm = CreateLeftPanelViewModel();
        vm.PdkManager.RegisterPdk("Foundry Pdk", null, isBundled: true, componentCount: 1);
        var template = new ComponentTemplate { Name = "X", PdkSource = "Foundry Pdk" };

        vm.CanEditTemplate(template).ShouldBeFalse();
    }

    [Fact]
    public void CanEditTemplate_false_whenTheTemplatesPdkIsNotCurrentlyLoaded()
    {
        var vm = CreateLeftPanelViewModel();
        var template = new ComponentTemplate { Name = "X", PdkSource = "Never Loaded" };

        vm.CanEditTemplate(template).ShouldBeFalse();
    }

    [Fact]
    public async Task EditCustomComponent_forBundledTemplate_doesNothing()
    {
        var vm = CreateLeftPanelViewModel(CreateUserPdkStore());
        vm.PdkManager.RegisterPdk("Foundry Pdk", null, isBundled: true, componentCount: 1);
        var template = new ComponentTemplate { Name = "Grating Coupler", PdkSource = "Foundry Pdk" };

        var showCalls = 0;
        vm.ShowNewComponentWindowAsync = _ => { showCalls++; return Task.CompletedTask; };

        await vm.EditCustomComponentCommand.ExecuteAsync(template);

        showCalls.ShouldBe(0);
    }

    [Fact]
    public async Task EditCustomComponent_forCustomTemplate_opensTheAssistantPrefilledForEdit()
    {
        // Same UserPdkStore instance for both the assistant's deps and the on-disk PDK, so
        // NewComponentViewModel's PdkChoices (read from the store) actually contain "ActiveProc"
        // — a real bug caught by this test: two separate stores would silently leave PdkChoices
        // empty and LoadForEdit unable to find a match.
        var store = CreateUserPdkStore();
        var vm = CreateLeftPanelViewModel(store);
        var (filePath, pdkName, draft) = SaveUserPdk(store, "ActiveProc");
        vm.RegisterSavedCustomComponent(draft, pdkName, filePath);
        var template = vm.AllTemplates.First(t => t.Name == draft.Name);

        NewComponentViewModel? shownVm = null;
        vm.ShowNewComponentWindowAsync = shown => { shownVm = shown; return Task.CompletedTask; };

        await vm.EditCustomComponentCommand.ExecuteAsync(template);

        shownVm.ShouldNotBeNull();
        shownVm!.IsEditMode.ShouldBeTrue();
        shownVm.ComponentName.ShouldBe(draft.Name);
        shownVm.Code.ShouldBe(draft.RawCode);
    }
}
