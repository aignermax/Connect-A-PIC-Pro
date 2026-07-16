using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Hierarchy;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core;
using CAP_Core.Components.Creation;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

/// <summary>
/// Failure robustness of the revert-to-bundled paths (PR #742 review, findings 3 and 5): a
/// failed PDK revert must be all-or-nothing (never "fork trashed AND original missing"), and
/// a component revert must rewrite the fork file in a single operation.
/// </summary>
public class BundledPdkRevertRobustnessTests : IDisposable
{
    private const string BundledPdkName = "Foundry PDK";
    private const string Code = "import gdsfactory as gf\ncomponent = gf.components.coupler()";

    private readonly string _testPrefsPath;
    private readonly string _userPdkRoot;
    private readonly string _bundledDir;
    private readonly ErrorConsoleService _errorConsole = new();
    private readonly UserPreferencesService _preferencesService;

    public BundledPdkRevertRobustnessTests()
    {
        var id = Guid.NewGuid().ToString("N");
        _testPrefsPath = Path.Combine(Path.GetTempPath(), $"RevertRobustPrefs_{id}.json");
        _userPdkRoot = Path.Combine(Path.GetTempPath(), $"RevertRobustRoot_{id}");
        _bundledDir = Path.Combine(Path.GetTempPath(), $"RevertRobustBundled_{id}");
        _preferencesService = new UserPreferencesService(_testPrefsPath);
    }

    public void Dispose()
    {
        try { File.Delete(_testPrefsPath); } catch { }
        foreach (var dir in new[] { _userPdkRoot, _bundledDir })
        {
            if (Directory.Exists(dir))
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }

    private static PdkComponentDraft RawCodeComponent(string name, string rawCode = Code) => new()
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

    private (LeftPanelViewModel leftPanel, UserPdkStore store, string bundledPath) CreateLeftPanelWithBundledPdk()
    {
        Directory.CreateDirectory(_bundledDir);
        var bundledPath = Path.Combine(_bundledDir, "foundry-pdk.json");
        new PdkJsonSaver().SaveToFile(new PdkDraft
        {
            Name = BundledPdkName,
            Backend = "gdsfactory",
            Process = new ProcessDefinition { Name = "Foundry Process" },
            Components = new List<PdkComponentDraft>
            {
                RawCodeComponent("Bundled Coupler"),
                RawCodeComponent("Bundled Straight"),
            },
        }, bundledPath);

        var canvas = new DesignCanvasViewModel();
        var libraryManager = new GroupLibraryManager();
        var store = new UserPdkStore(_userPdkRoot, new PdkJsonSaver(), new PdkLoader());
        var deps = new AddCustomComponentDependencies(
            new ComponentGeometryExtractor(
                new Mock<IComponentPreviewRenderer>().Object, new Mock<IComponentPreviewRenderer>().Object),
            Fdtd: null, UserPdkStore: store);

        var leftPanel = new LeftPanelViewModel(
            canvas, libraryManager, new PdkLoader(), _preferencesService,
            new HierarchyPanelViewModel(canvas), new PdkManagerViewModel(),
            new ComponentLibraryViewModel(libraryManager),
            errorConsole: _errorConsole, addCustomComponentDeps: deps);
        leftPanel.LoadBundledPdksFrom(_bundledDir);
        return (leftPanel, store, bundledPath);
    }

    private async Task<PdkInfoViewModel> CreateShadowingForkAsync(LeftPanelViewModel leftPanel, UserPdkStore store)
    {
        store.SaveToNamedPdk(BundledPdkName, new ProcessDefinition { Name = "Foundry Process" },
            RawCodeComponent("Bundled Coupler", Code + "\n# user tweak"), "gdsfactory", null);
        await leftPanel.ReloadUserPdksAtStartupAsync(_userPdkRoot);
        return leftPanel.PdkManager.LoadedPdks.Single(p => p.Name == BundledPdkName);
    }

    // -------------------------------------------------- finding 3: all-or-nothing PDK revert

    [Fact]
    public async Task RevertShadowFork_whenTheBundledOriginalIsUnreadable_failsWithoutChangingAnything()
    {
        // The bundled JSON recorded at startup can disappear (app update) or be unreadable.
        // The revert must then fail as a unit: the fork must NOT be trashed or deregistered —
        // never a half state where neither the fork nor the original is in the library.
        var (leftPanel, store, bundledPath) = CreateLeftPanelWithBundledPdk();
        var forkRow = await CreateShadowingForkAsync(leftPanel, store);
        var forkPath = forkRow.FilePath!;
        File.Delete(bundledPath); // simulate external damage to the built-in installation

        leftPanel.RevertShadowForkToBundled(forkRow).ShouldBeFalse();

        File.Exists(forkPath).ShouldBeTrue("the fork file must not be moved to trash when the revert fails");
        var row = leftPanel.PdkManager.LoadedPdks.Single(p => p.Name == BundledPdkName);
        row.IsBundled.ShouldBeFalse("the fork must still be the registered entry");
        row.ShadowsBundledPdk.ShouldBeTrue();
        leftPanel.AllTemplates.Single(t => t.PdkSource == BundledPdkName && t.Name == "Bundled Coupler")
            .RawCode.ShouldContain("# user tweak"); // the library must keep showing the fork's components
        _errorConsole.Entries.ShouldContain(e => e.Message.Contains("Could not restore bundled PDK"));
    }

    // ---------------------------------------------- finding 5: single-write component revert

    [Fact]
    public async Task ComponentRevert_rewritesTheForkFile_inASingleOperation_withBackup()
    {
        var (leftPanel, store, _) = CreateLeftPanelWithBundledPdk();
        await CreateShadowingForkAsync(leftPanel, store);
        var forkPath = store.ResolveNamedPath(BundledPdkName);
        var customized = leftPanel.AllTemplates.Single(
            t => t.PdkSource == BundledPdkName && t.Name == "Bundled Coupler");

        leftPanel.RemoveCustomComponentCommand.Execute(customized);

        var fork = new PdkLoader().LoadFromFileForEditing(forkPath);
        fork.Components.Single(c => c.Name == "Bundled Coupler").RawCode.ShouldBe(Code,
            "the component must be reverted to the foundry definition");
        Directory.GetFiles(Path.Combine(_userPdkRoot, ".trash"), "*.json").Length.ShouldBe(1,
            "exactly one pre-revert backup is written — a second trash file would betray a remove+append double write");
    }

    [Fact]
    public void ReplaceComponent_missingFile_returnsFalse_andWritesNothing()
    {
        var store = new UserPdkStore(_userPdkRoot, new PdkJsonSaver(), new PdkLoader());
        var missing = Path.Combine(_userPdkRoot, "gone.json");

        store.ReplaceComponent(missing, RawCodeComponent("X")).ShouldBeFalse();

        File.Exists(missing).ShouldBeFalse();
        Directory.Exists(Path.Combine(_userPdkRoot, ".trash")).ShouldBeFalse();
    }
}
