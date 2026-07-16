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
using CAP_Core;
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
/// Covers the "fork on save, not on open" semantics for bundled (foundry) PDKs plus the
/// revert-to-foundry-truth delete semantics and the startup fork-shadow:
/// opening the editor on a bundled component must leave the disk untouched, saving must fork
/// the whole PDK into user-pdks and shadow the bundled entry, deleting the fork (or a forked
/// component) must restore the built-in original, and a fork found at startup must shadow the
/// bundled PDK instead of being skipped as a name duplicate.
/// </summary>
public class BundledPdkForkSemanticsTests : IDisposable
{
    private const string BundledPdkName = "Foundry PDK";
    private const string BundledCouplerCode = "import gdsfactory as gf\ncomponent = gf.components.coupler()";

    private readonly string _testPrefsPath;
    private readonly string _userPdkRoot;
    private readonly string _bundledDir;
    private readonly string _externalDir;
    private readonly ErrorConsoleService _errorConsole = new();
    private readonly UserPreferencesService _preferencesService;

    public BundledPdkForkSemanticsTests()
    {
        var id = Guid.NewGuid().ToString("N");
        _testPrefsPath = Path.Combine(Path.GetTempPath(), $"BundledForkPrefs_{id}.json");
        _userPdkRoot = Path.Combine(Path.GetTempPath(), $"BundledForkRoot_{id}");
        _bundledDir = Path.Combine(Path.GetTempPath(), $"BundledForkBundled_{id}");
        _externalDir = Path.Combine(Path.GetTempPath(), $"BundledForkExternal_{id}");
        _preferencesService = new UserPreferencesService(_testPrefsPath);
    }

    public void Dispose()
    {
        if (File.Exists(_testPrefsPath))
        {
            try { File.Delete(_testPrefsPath); } catch { }
        }
        foreach (var dir in new[] { _userPdkRoot, _bundledDir, _externalDir })
        {
            if (Directory.Exists(dir))
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }

    private static NazcaPreviewResult PreviewOk() => new()
    {
        Success = true, XMin = 0, YMin = 0, XMax = 8, YMax = 1.5,
        Pins = new List<NazcaPreviewPin>
        {
            new() { Name = "o1", X = 0, Y = 0.75, Angle = 180 },
            new() { Name = "o2", X = 8, Y = 0.75, Angle = 0 },
        },
    };

    private static PdkComponentDraft RawCodeComponent(string name, string rawCode) => new()
    {
        Name = name,
        Category = "Test",
        RawCode = rawCode,
        RawCodeBackend = "gdsfactory",
        WidthMicrometers = 10,
        HeightMicrometers = 2,
        Pins = new List<PhysicalPinDraft>
        {
            new() { Name = "o1", OffsetXMicrometers = 0, OffsetYMicrometers = 1, AngleDegrees = 180 },
            new() { Name = "o2", OffsetXMicrometers = 10, OffsetYMicrometers = 1, AngleDegrees = 0 },
        },
    };

    private string WriteBundledPdk(ProcessDefinition? process = null)
    {
        Directory.CreateDirectory(_bundledDir);
        var path = Path.Combine(_bundledDir, "foundry-pdk.json");
        new PdkJsonSaver().SaveToFile(new PdkDraft
        {
            Name = BundledPdkName,
            Backend = "gdsfactory",
            Process = process ?? new ProcessDefinition { Name = "Foundry Process" },
            Components = new List<PdkComponentDraft>
            {
                RawCodeComponent("Bundled Coupler", BundledCouplerCode),
                RawCodeComponent("Bundled Straight", "import gdsfactory as gf\ncomponent = gf.components.straight()"),
            },
        }, path);
        return path;
    }

    private (LeftPanelViewModel leftPanel, UserPdkStore store) CreateLeftPanel()
    {
        var canvas = new DesignCanvasViewModel();
        var libraryManager = new GroupLibraryManager();
        var store = new UserPdkStore(_userPdkRoot, new PdkJsonSaver(), new PdkLoader());

        var nazca = new Mock<IComponentPreviewRenderer>();
        var gds = new Mock<IComponentPreviewRenderer>();
        gds.Setup(g => g.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PreviewOk());
        var deps = new AddCustomComponentDependencies(
            new ComponentGeometryExtractor(nazca.Object, gds.Object), Fdtd: null, UserPdkStore: store);

        var leftPanel = new LeftPanelViewModel(
            canvas, libraryManager, new PdkLoader(), _preferencesService,
            new HierarchyPanelViewModel(canvas), new PdkManagerViewModel(),
            new ComponentLibraryViewModel(libraryManager),
            errorConsole: _errorConsole, addCustomComponentDeps: deps);
        return (leftPanel, store);
    }

    private (LeftPanelViewModel leftPanel, UserPdkStore store, string bundledPath) CreateLeftPanelWithBundledPdk()
    {
        var bundledPath = WriteBundledPdk();
        var (leftPanel, store) = CreateLeftPanel();
        leftPanel.LoadBundledPdksFrom(_bundledDir);
        return (leftPanel, store, bundledPath);
    }

    private static async Task<NewComponentViewModel> OpenEditorAsync(LeftPanelViewModel leftPanel, string componentName)
    {
        NewComponentViewModel? shownVm = null;
        leftPanel.ShowNewComponentWindowAsync = shown => { shownVm = shown; return Task.CompletedTask; };
        var template = leftPanel.AllTemplates.First(t => t.Name == componentName);
        await leftPanel.EditCustomComponentCommand.ExecuteAsync(template);
        shownVm.ShouldNotBeNull("the editor window must open for a bundled component");
        return shownVm!;
    }

    private static async Task SaveEditorAsync(NewComponentViewModel editor)
    {
        editor.ConfirmOverwrite = (_, _) => Task.FromResult(true);
        await editor.RunPreviewCommand.ExecuteAsync(null);
        await editor.SaveCommand.ExecuteAsync(null);
        editor.SavedDraft.ShouldNotBeNull($"save must succeed (status: {editor.StatusText})");
    }

    // ------------------------------------------------------------------ (a) open without fork

    [Fact]
    public async Task EditBundledComponent_opensEditor_withoutCreatingAnyForkFile()
    {
        var (leftPanel, store, _) = CreateLeftPanelWithBundledPdk();

        var editor = await OpenEditorAsync(leftPanel, "Bundled Coupler");

        editor.IsEditMode.ShouldBeTrue();
        editor.Code.ShouldBe(BundledCouplerCode);
        store.NamedPdkExists(BundledPdkName).ShouldBeFalse(
            "just opening the editor must not fork the bundled PDK onto disk");
        Directory.Exists(_userPdkRoot).ShouldBeFalse("no user-pdks artifact may exist before the first save");
        leftPanel.PdkManager.LoadedPdks.ShouldContain(p => p.Name == BundledPdkName && p.IsBundled,
            "the library must keep showing the untouched bundled PDK while the editor is open");
    }

    // ------------------------------------------------------------------ (b) fork on save

    [Fact]
    public async Task SaveChanges_onBundledComponent_forksThePdk_andSwitchesLibraryToTheCopy()
    {
        var (leftPanel, store, bundledPath) = CreateLeftPanelWithBundledPdk();
        var bundledJsonBefore = File.ReadAllText(bundledPath);

        var editor = await OpenEditorAsync(leftPanel, "Bundled Coupler");
        editor.Code = BundledCouplerCode + "\n# user tweak";
        await SaveEditorAsync(editor);

        store.NamedPdkExists(BundledPdkName).ShouldBeTrue("saving must fork the bundled PDK into user-pdks");
        File.ReadAllText(bundledPath).ShouldBe(bundledJsonBefore, "the bundled JSON must never be written");

        var fork = new PdkLoader().LoadFromFileForEditing(store.ResolveNamedPath(BundledPdkName));
        fork.Components.Single(c => c.Name == "Bundled Coupler").RawCode.ShouldContain("# user tweak");
        fork.Components.ShouldContain(c => c.Name == "Bundled Straight",
            "the fork is a full copy of the PDK, not just the edited component");

        var rows = leftPanel.PdkManager.LoadedPdks.Where(p => p.Name == BundledPdkName).ToList();
        rows.Count.ShouldBe(1, "the fork must shadow (replace) the bundled entry, not sit next to it");
        rows[0].IsBundled.ShouldBeFalse();
        rows[0].ShadowsBundledPdk.ShouldBeTrue();

        var templates = leftPanel.AllTemplates.Where(t => t.PdkSource == BundledPdkName).ToList();
        templates.Count.ShouldBe(2);
        templates.ShouldAllBe(t => t.IsCustom, "after the fork, the library shows the editable copy");
        templates.Single(t => t.Name == "Bundled Coupler").RawCode.ShouldContain("# user tweak");
    }

    [Fact]
    public async Task ClosingEditor_withoutSave_leavesNothingOnDisk()
    {
        var (leftPanel, store, _) = CreateLeftPanelWithBundledPdk();

        var editor = await OpenEditorAsync(leftPanel, "Bundled Coupler");
        editor.Code = BundledCouplerCode + "\n# abandoned tweak";
        // Window is closed without hitting "Save changes" — no further calls.

        store.NamedPdkExists(BundledPdkName).ShouldBeFalse();
        Directory.Exists(_userPdkRoot).ShouldBeFalse();
        leftPanel.AllTemplates.Where(t => t.PdkSource == BundledPdkName)
            .ShouldAllBe(t => !t.IsCustom, "the library must still show the untouched bundled components");
    }

    // ------------------------------------------------------------------ (c) delete fork = restore original

    [Fact]
    public async Task DeletingTheForkedPdk_movesTheCopyToTrash_andRestoresTheBundledOriginal()
    {
        var (leftPanel, store, _) = CreateLeftPanelWithBundledPdk();
        var editor = await OpenEditorAsync(leftPanel, "Bundled Coupler");
        editor.Code = BundledCouplerCode + "\n# user tweak";
        await SaveEditorAsync(editor);
        var forkRow = leftPanel.PdkManager.LoadedPdks.Single(p => p.Name == BundledPdkName);
        var forkPath = forkRow.FilePath!;

        leftPanel.GetBundledOriginalComponentCount(BundledPdkName).ShouldBe(2,
            "the confirm prompt needs the built-in original's component count");
        leftPanel.RevertShadowForkToBundled(forkRow).ShouldBeTrue();

        File.Exists(forkPath).ShouldBeFalse("the fork file must be moved away");
        Directory.GetFiles(Path.Combine(_userPdkRoot, ".trash"), "*.json")
            .ShouldNotBeEmpty("the user's copy must be recoverable from the trash");

        var restored = leftPanel.PdkManager.LoadedPdks.Single(p => p.Name == BundledPdkName);
        restored.IsBundled.ShouldBeTrue("the built-in original must reappear in the library");
        restored.ComponentCount.ShouldBe(2);

        var templates = leftPanel.AllTemplates.Where(t => t.PdkSource == BundledPdkName).ToList();
        templates.Count.ShouldBe(2);
        templates.ShouldAllBe(t => !t.IsCustom);
        templates.Single(t => t.Name == "Bundled Coupler").RawCode.ShouldNotContain("# user tweak");
        leftPanel.CanDeleteTemplate(templates[0]).ShouldBeFalse("bundled components never show an X");
    }

    [Fact]
    public async Task DeletingAForkedComponent_revertsItToTheBundledDefinition()
    {
        var (leftPanel, store, _) = CreateLeftPanelWithBundledPdk();
        var editor = await OpenEditorAsync(leftPanel, "Bundled Coupler");
        editor.Code = BundledCouplerCode + "\n# user tweak";
        await SaveEditorAsync(editor);

        var edited = leftPanel.AllTemplates.Single(t => t.PdkSource == BundledPdkName && t.Name == "Bundled Coupler");
        leftPanel.IsComponentRevertToBundled(edited).ShouldBeTrue(
            "the confirm prompt must announce a restore, not a plain delete");
        leftPanel.RemoveCustomComponentCommand.Execute(edited);

        var fork = new PdkLoader().LoadFromFileForEditing(store.ResolveNamedPath(BundledPdkName));
        var reverted = fork.Components.Single(c => c.Name == "Bundled Coupler");
        reverted.RawCode.ShouldBe(BundledCouplerCode, "the fork's component must revert to the foundry definition");

        var template = leftPanel.AllTemplates.Single(t => t.PdkSource == BundledPdkName && t.Name == "Bundled Coupler");
        template.RawCode.ShouldBe(BundledCouplerCode);
        Directory.GetFiles(Path.Combine(_userPdkRoot, ".trash"), "*.json")
            .ShouldNotBeEmpty("the customized state must be backed up before the revert");
    }

    [Fact]
    public async Task DeletingAComponentAddedToTheFork_deletesItPlainly_becauseThereIsNoBundledOriginal()
    {
        var (leftPanel, store, _) = CreateLeftPanelWithBundledPdk();
        var editor = await OpenEditorAsync(leftPanel, "Bundled Coupler");
        await SaveEditorAsync(editor);
        var forkPath = store.ResolveNamedPath(BundledPdkName);
        var added = RawCodeComponent("My New Comp", "import gdsfactory as gf\ncomponent = gf.components.taper()");
        store.AppendToExistingPdk(forkPath, added);
        leftPanel.RegisterSavedCustomComponent(added, BundledPdkName, forkPath);

        var template = leftPanel.AllTemplates.Single(t => t.Name == "My New Comp");
        leftPanel.IsComponentRevertToBundled(template).ShouldBeFalse();
        leftPanel.RemoveCustomComponentCommand.Execute(template);

        leftPanel.AllTemplates.ShouldNotContain(t => t.Name == "My New Comp");
        new PdkLoader().LoadFromFileForEditing(forkPath).Components
            .ShouldNotContain(c => c.Name == "My New Comp");
    }

    // ------------------------------------------------------------------ (c2) X only on divergent fork components

    [Fact]
    public async Task ForkComponents_onlyTheEditedOne_isDeletable()
    {
        var (leftPanel, _, _) = CreateLeftPanelWithBundledPdk();
        var editor = await OpenEditorAsync(leftPanel, "Bundled Coupler");
        editor.Code = BundledCouplerCode + "\n# user tweak";
        await SaveEditorAsync(editor);

        var edited = leftPanel.AllTemplates.Single(t => t.PdkSource == BundledPdkName && t.Name == "Bundled Coupler");
        var untouched = leftPanel.AllTemplates.Single(t => t.PdkSource == BundledPdkName && t.Name == "Bundled Straight");

        leftPanel.CanDeleteTemplate(edited).ShouldBeTrue(
            "the edited component diverges from the bundled original — offer Restore Original");
        edited.IsDeletable.ShouldBeTrue("the ✕ must appear on the edited component");
        leftPanel.CanDeleteTemplate(untouched).ShouldBeFalse(
            "an untouched fork component is identical to the bundled original — there is nothing to restore");
        untouched.IsDeletable.ShouldBeFalse("no ✕ on identical fork components");
        leftPanel.CanEditTemplate(untouched).ShouldBeTrue("the ✏ stays on every fork component");
    }

    [Fact]
    public async Task ComponentAddedToTheFork_isDeletable_becauseTheOriginalNeverHadIt()
    {
        var (leftPanel, store, _) = CreateLeftPanelWithBundledPdk();
        var editor = await OpenEditorAsync(leftPanel, "Bundled Coupler");
        editor.Code = BundledCouplerCode + "\n# user tweak";
        await SaveEditorAsync(editor);
        var forkPath = store.ResolveNamedPath(BundledPdkName);
        var added = RawCodeComponent("My New Comp", "import gdsfactory as gf\ncomponent = gf.components.taper()");
        store.AppendToExistingPdk(forkPath, added);
        leftPanel.RegisterSavedCustomComponent(added, BundledPdkName, forkPath);

        var template = leftPanel.AllTemplates.Single(t => t.Name == "My New Comp");
        leftPanel.CanDeleteTemplate(template).ShouldBeTrue(
            "a component the bundled original never had gets the plain delete-to-trash ✕");
        template.IsDeletable.ShouldBeTrue();
    }

    [Fact]
    public async Task RevertingTheEditedComponent_hidesItsDeleteAction_again()
    {
        var (leftPanel, _, _) = CreateLeftPanelWithBundledPdk();
        var editor = await OpenEditorAsync(leftPanel, "Bundled Coupler");
        editor.Code = BundledCouplerCode + "\n# user tweak";
        await SaveEditorAsync(editor);
        var edited = leftPanel.AllTemplates.Single(t => t.PdkSource == BundledPdkName && t.Name == "Bundled Coupler");
        edited.IsDeletable.ShouldBeTrue("sanity: the edited component shows the ✕ before the revert");

        leftPanel.RemoveCustomComponentCommand.Execute(edited);

        var reverted = leftPanel.AllTemplates.Single(t => t.PdkSource == BundledPdkName && t.Name == "Bundled Coupler");
        leftPanel.CanDeleteTemplate(reverted).ShouldBeFalse(
            "after the revert the component matches the bundled original again — the ✕ disappears");
        reverted.IsDeletable.ShouldBeFalse();
    }

    [Fact]
    public void PlainCustomPdkComponents_stayDeletable_withoutABundledOriginal()
    {
        var (leftPanel, store) = CreateLeftPanel();
        var component = RawCodeComponent("Solo Comp", BundledCouplerCode);
        var path = store.SaveToNamedPdk("SoloLib", new ProcessDefinition { Name = "P1" },
            component, "gdsfactory", null);
        leftPanel.RegisterSavedCustomComponent(component, "SoloLib", path);

        var template = leftPanel.AllTemplates.Single(t => t.Name == "Solo Comp");
        leftPanel.CanDeleteTemplate(template).ShouldBeTrue(
            "components of a plain custom PDK keep the normal delete-to-trash ✕");
        template.IsDeletable.ShouldBeTrue();
    }

    // ------------------------------------------------------------------ (d) startup fork-shadow

    [Fact]
    public async Task StartupReload_userForkWithBundledName_shadowsTheBundledPdk_withoutDuplicateWarning()
    {
        WriteBundledPdk();
        var (leftPanel, store) = CreateLeftPanel();
        leftPanel.LoadBundledPdksFrom(_bundledDir);
        store.SaveToNamedPdk(BundledPdkName, new ProcessDefinition { Name = "Foundry Process" },
            RawCodeComponent("Bundled Coupler", BundledCouplerCode + "\n# forked"), "gdsfactory", null);

        await leftPanel.ReloadUserPdksAtStartupAsync(_userPdkRoot);

        var rows = leftPanel.PdkManager.LoadedPdks.Where(p => p.Name == BundledPdkName).ToList();
        rows.Count.ShouldBe(1, "the fork must shadow the bundled PDK, exactly like in the session that created it");
        rows[0].IsBundled.ShouldBeFalse();
        rows[0].ShadowsBundledPdk.ShouldBeTrue();

        var templates = leftPanel.AllTemplates.Where(t => t.PdkSource == BundledPdkName).ToList();
        templates.ShouldAllBe(t => t.IsCustom, "the visible components come from the user's copy");
        templates.Single(t => t.Name == "Bundled Coupler").RawCode.ShouldContain("# forked");

        _errorConsole.Entries.ShouldNotContain(e => e.Message.Contains("duplicates an already-loaded"),
            "a fork of a bundled PDK is expected at startup and must not be logged as a duplicate");
    }

    [Fact]
    public async Task ShadowFork_keepsTheFoundryReferenceRole_inTheLayerConsistencyCheck()
    {
        // The fork carries the same name and process as the foundry PDK it shadows — it must
        // inherit the foundry's role as layer-consistency reference, so an unrelated custom PDK
        // with renumbered GDS layers can never lock the (forked) foundry PDK out.
        static ProcessDefinition FullProcess(string name, int waveguideLayer) => new()
        {
            Name = name,
            CoreThicknessNm = 222,
            Materials = new List<ProcessMaterial>
            {
                new() { Name = "Si", Role = "core" },
                new() { Name = "SiO2", Role = "cladding" },
            },
            Layers = new List<ProcessLayer> { new() { Name = "WAVEGUIDE", Layer = waveguideLayer, Datatype = 0 } },
        };

        WriteBundledPdk(FullProcess("Foundry Process", 1));
        var (leftPanel, store) = CreateLeftPanel();
        leftPanel.LoadBundledPdksFrom(_bundledDir);

        // A layer-renumbered custom PDK that is registered BEFORE the fork shadows the bundled entry.
        var renumberedComponent = RawCodeComponent("Renumbered Straight", BundledCouplerCode);
        var renumberedPath = store.SaveToNamedPdk("RenumberedLib", FullProcess("Foundry Process", 999),
            renumberedComponent, "gdsfactory", null);
        leftPanel.RegisterSavedCustomComponent(renumberedComponent, "RenumberedLib", renumberedPath);

        store.SaveToNamedPdk(BundledPdkName, FullProcess("Foundry Process", 1),
            RawCodeComponent("Bundled Coupler", BundledCouplerCode + "\n# forked"), "gdsfactory", null);
        await leftPanel.ReloadUserPdksAtStartupAsync(_userPdkRoot);

        var forkDraft = leftPanel.GetLoadedPdkDrafts().First(d => d.Name == BundledPdkName);
        var fingerprint = ProcessFingerprintFactory.From(forkDraft);
        fingerprint.IsSpecified.ShouldBeTrue("sanity: the fork carries the foundry's full process");
        leftPanel.ApplyActiveProcess(new ActiveProcessSelection(
            DisplayName: "Foundry Process",
            Fingerprint: fingerprint,
            MemberPdkNames: new List<string> { "RenumberedLib", BundledPdkName },
            IsPlayground: false));

        leftPanel.PdkManager.LoadedPdks.Single(p => p.Name == BundledPdkName).IsEnabled.ShouldBeTrue(
            "the fork inherits the foundry PDK's reference role and must stay a live member");
        leftPanel.PdkManager.LoadedPdks.Single(p => p.Name == "RenumberedLib").IsEnabled.ShouldBeFalse(
            "the layer-renumbered custom PDK must fall out against the (forked) foundry reference");
    }

    [Fact]
    public async Task StartupReload_collisionWithANonBundledPdk_isStillSkippedWithAWarning()
    {
        var (leftPanel, _) = CreateLeftPanel();
        var rootStore = new UserPdkStore(_userPdkRoot, new PdkJsonSaver(), new PdkLoader());
        rootStore.SaveToNamedPdk("TwinLib", new ProcessDefinition { Name = "P1" },
            RawCodeComponent("First Twin", BundledCouplerCode), "gdsfactory", null);
        var externalStore = new UserPdkStore(_externalDir, new PdkJsonSaver(), new PdkLoader());
        var externalPath = externalStore.SaveToNamedPdk("TwinLib", new ProcessDefinition { Name = "P2" },
            RawCodeComponent("Second Twin", BundledCouplerCode), "gdsfactory", null);
        _preferencesService.AddUserPdkPath(externalPath);

        await leftPanel.ReloadUserPdksAtStartupAsync(_userPdkRoot);

        leftPanel.PdkManager.LoadedPdks.Count(p => p.Name == "TwinLib").ShouldBe(1,
            "a name collision between two non-bundled PDKs is not a fork-shadow and must still be skipped");
        _errorConsole.Entries.ShouldContain(e => e.Message.Contains("duplicates an already-loaded"));
    }
}
