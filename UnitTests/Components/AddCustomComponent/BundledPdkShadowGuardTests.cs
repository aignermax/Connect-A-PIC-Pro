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
/// Guards around the bundled-PDK shadow mechanism (PR #742 review, findings 1, 6, 8): only a
/// save that actually executed the deferred fork may displace a loaded bundled PDK; a failed
/// fork registration must leave the bundled entry untouched; and the interactive Load-PDK path
/// shadows a fork file exactly like the startup path instead of rejecting it as a duplicate.
/// </summary>
public class BundledPdkShadowGuardTests : IDisposable
{
    private const string BundledPdkName = "Foundry PDK";
    private const string Code = "import gdsfactory as gf\ncomponent = gf.components.coupler()";

    private readonly string _testPrefsPath;
    private readonly string _userPdkRoot;
    private readonly string _bundledDir;
    private readonly string _externalDir;
    private readonly ErrorConsoleService _errorConsole = new();
    private readonly UserPreferencesService _preferencesService;

    public BundledPdkShadowGuardTests()
    {
        var id = Guid.NewGuid().ToString("N");
        _testPrefsPath = Path.Combine(Path.GetTempPath(), $"ShadowGuardPrefs_{id}.json");
        _userPdkRoot = Path.Combine(Path.GetTempPath(), $"ShadowGuardRoot_{id}");
        _bundledDir = Path.Combine(Path.GetTempPath(), $"ShadowGuardBundled_{id}");
        _externalDir = Path.Combine(Path.GetTempPath(), $"ShadowGuardExternal_{id}");
        _preferencesService = new UserPreferencesService(_testPrefsPath);
    }

    public void Dispose()
    {
        try { File.Delete(_testPrefsPath); } catch { }
        foreach (var dir in new[] { _userPdkRoot, _bundledDir, _externalDir })
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

    private (LeftPanelViewModel leftPanel, UserPdkStore store) CreateLeftPanelWithBundledPdk()
    {
        Directory.CreateDirectory(_bundledDir);
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
        }, Path.Combine(_bundledDir, "foundry-pdk.json"));

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
        return (leftPanel, store);
    }

    // ---------------------------------------------------------------- finding 1: name != fork

    [Fact]
    public void SaveWithoutForkFlag_underABundledName_neverDeregistersTheBundledPdk()
    {
        // A brand-new 1-component user PDK that merely SHARES a bundled PDK's name is not a
        // fork: registering its saved component must not displace the built-in library.
        var (leftPanel, store) = CreateLeftPanelWithBundledPdk();
        var solo = RawCodeComponent("Solo Comp");
        var path = store.SaveToNamedPdk(BundledPdkName, new ProcessDefinition { Name = "Other" },
            solo, "gdsfactory", null);

        leftPanel.RegisterSavedCustomComponent(solo, BundledPdkName, path);

        var bundledRow = leftPanel.PdkManager.LoadedPdks.Single(p => p.Name == BundledPdkName);
        bundledRow.IsBundled.ShouldBeTrue("the bundled PDK must stay registered");
        bundledRow.ComponentCount.ShouldBe(2);
        leftPanel.AllTemplates.Count(t => t.PdkSource == BundledPdkName && !t.IsCustom).ShouldBe(2,
            "the built-in components must all remain in the library");
        _errorConsole.Entries.ShouldContain(e => e.Message.Contains("built-in"),
            "the rejected registration must be reported, not silent");
    }

    // ----------------------------------------------------- finding 6: no shadow without a fork

    [Fact]
    public void SaveWithForkFlag_whoseForkFileCannotBeLoaded_keepsTheBundledPdkUntouched()
    {
        var (leftPanel, _) = CreateLeftPanelWithBundledPdk();
        Directory.CreateDirectory(_userPdkRoot);
        var corruptForkPath = Path.Combine(_userPdkRoot, "foundry-pdk.json");
        File.WriteAllText(corruptForkPath, "{ this is not a valid PDK json");

        leftPanel.RegisterSavedCustomComponent(
            RawCodeComponent("Edited Coupler"), BundledPdkName, corruptForkPath, savedViaBundledFork: true);

        var rows = leftPanel.PdkManager.LoadedPdks.Where(p => p.Name == BundledPdkName).ToList();
        rows.Count.ShouldBe(1, "no fork row may appear when the fork file cannot be loaded");
        rows[0].IsBundled.ShouldBeTrue("the bundled PDK must never be deregistered before the fork is registered");
        leftPanel.AllTemplates.Count(t => t.PdkSource == BundledPdkName && !t.IsCustom).ShouldBe(2);
        _errorConsole.Entries.ShouldContain(e => e.Message.Contains("could not be loaded"),
            "the failed fork registration must be reported explicitly, not as a silent success");
    }

    // ------------------------------------------------- finding 8: interactive load = fork-shadow

    [Fact]
    public async Task InteractiveLoadPdk_ofAForkFile_shadowsTheBundledPdk_likeTheStartupPath()
    {
        var (leftPanel, store) = CreateLeftPanelWithBundledPdk();
        var forkPath = store.SaveToNamedPdk(BundledPdkName, new ProcessDefinition { Name = "Foundry Process" },
            RawCodeComponent("Bundled Coupler", Code + "\n# forked"), "gdsfactory", null);
        var dialog = new Mock<IFileDialogService>();
        dialog.Setup(d => d.ShowOpenFileDialogAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(forkPath);
        leftPanel.FileDialogService = dialog.Object;
        var statuses = new List<string>();
        leftPanel.UpdateStatus = statuses.Add;

        await leftPanel.LoadPdkCommand.ExecuteAsync(null);

        var rows = leftPanel.PdkManager.LoadedPdks.Where(p => p.Name == BundledPdkName).ToList();
        rows.Count.ShouldBe(1, "the fork must shadow (replace) the bundled entry, not be rejected");
        rows[0].IsBundled.ShouldBeFalse();
        rows[0].ShadowsBundledPdk.ShouldBeTrue();
        leftPanel.AllTemplates.Single(t => t.PdkSource == BundledPdkName && t.Name == "Bundled Coupler")
            .RawCode.ShouldContain("# forked");
        statuses.ShouldNotContain(s => s.Contains("already loaded"),
            "the misleading duplicate rejection must be gone for fork files");
    }

    [Fact]
    public async Task InteractiveLoadPdk_nameCollisionWithANonBundledPdk_isStillRejected()
    {
        var (leftPanel, store) = CreateLeftPanelWithBundledPdk();
        var first = RawCodeComponent("First Twin");
        var firstPath = store.SaveToNamedPdk("TwinLib", new ProcessDefinition { Name = "P1" },
            first, "gdsfactory", null);
        leftPanel.RegisterSavedCustomComponent(first, "TwinLib", firstPath);
        var externalStore = new UserPdkStore(_externalDir, new PdkJsonSaver(), new PdkLoader());
        var externalPath = externalStore.SaveToNamedPdk("TwinLib", new ProcessDefinition { Name = "P2" },
            RawCodeComponent("Second Twin"), "gdsfactory", null);
        var dialog = new Mock<IFileDialogService>();
        dialog.Setup(d => d.ShowOpenFileDialogAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(externalPath);
        leftPanel.FileDialogService = dialog.Object;
        var statuses = new List<string>();
        leftPanel.UpdateStatus = statuses.Add;

        await leftPanel.LoadPdkCommand.ExecuteAsync(null);

        leftPanel.PdkManager.LoadedPdks.Count(p => p.Name == "TwinLib").ShouldBe(1,
            "a collision between two non-bundled PDKs is not a fork-shadow");
        statuses.ShouldContain(s => s.Contains("already loaded"));
    }
}
