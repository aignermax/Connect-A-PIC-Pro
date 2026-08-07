using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Hierarchy;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Components.Creation;
using CAP_Core.Components.Process;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

public class UserPdkStartupReloadTests : IDisposable
{
    private readonly string _testPrefsPath;
    private readonly string _userPdkRoot;
    private readonly string _externalDir;
    private readonly UserPreferencesService _preferencesService;
    private readonly LeftPanelViewModel _leftPanel;

    public UserPdkStartupReloadTests()
    {
        _testPrefsPath = Path.Combine(Path.GetTempPath(), $"UserPdkStartupReloadPrefs_{Guid.NewGuid():N}.json");
        _userPdkRoot = Path.Combine(Path.GetTempPath(), $"UserPdkStartupReloadRoot_{Guid.NewGuid():N}");
        _externalDir = Path.Combine(Path.GetTempPath(), $"UserPdkStartupReloadExternal_{Guid.NewGuid():N}");

        _preferencesService = new UserPreferencesService(_testPrefsPath);
        var canvas = new DesignCanvasViewModel();
        var groupLibrary = new GroupLibraryManager();
        var pdkLoader = new PdkLoader();

        _leftPanel = new LeftPanelViewModel(canvas, groupLibrary, pdkLoader, _preferencesService,
            new HierarchyPanelViewModel(canvas), new PdkManagerViewModel(),
            new ComponentLibraryViewModel(groupLibrary));
    }

    public void Dispose()
    {
        foreach (var path in new[] { _testPrefsPath })
        {
            if (File.Exists(path))
            {
                try { File.Delete(path); } catch { }
            }
        }
        foreach (var dir in new[] { _userPdkRoot, _externalDir })
        {
            if (Directory.Exists(dir))
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }

    private static ProcessDefinition SimpleProcess(string name) => new() { Name = name };

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

    private (string aPath, string bPath, string trashPath) SeedRoot()
    {
        var store = new UserPdkStore(_userPdkRoot, new PdkJsonSaver(), new PdkLoader());
        var aPath = store.SaveToNamedPdk("PdkA", SimpleProcess("Process A"), SimpleComponent("Straight A"), "nazca", null);
        var bPath = store.CreateNamedPdkWithProcess("PdkB", SimpleProcess("Process B"), "nazca", null);

        var trashDir = Path.Combine(_userPdkRoot, ".trash");
        Directory.CreateDirectory(trashDir);
        var trashPath = Path.Combine(trashDir, "trashed-pdk.json");
        new PdkJsonSaver().SaveToFile(new PdkDraft
        {
            Name = "TrashedPdk",
            Process = SimpleProcess("Trashed Process"),
            Components = new List<PdkComponentDraft> { SimpleComponent("Trashed Straight") },
        }, trashPath);

        return (aPath, bPath, trashPath);
    }

    [Fact]
    public async Task ReloadUserPdksAtStartupAsync_registers_dirScanned_and_remembered_pdks()
    {
        var (aPath, _, _) = SeedRoot();

        var externalStore = new UserPdkStore(_externalDir, new PdkJsonSaver(), new PdkLoader());
        var externalPath = externalStore.SaveToNamedPdk("PdkExternal", SimpleProcess("Process Ext"), SimpleComponent("Straight Ext"), "nazca", null);
        _preferencesService.AddUserPdkPath(externalPath);

        await _leftPanel.ReloadUserPdksAtStartupAsync(_userPdkRoot);

        var loaded = _leftPanel.PdkManager.LoadedPdks;
        loaded.ShouldContain(p => p.Name == "PdkA" && !p.IsBundled && p.ComponentCount == 1);
        loaded.ShouldContain(p => p.Name == "PdkB" && !p.IsBundled && p.ComponentCount == 0);
        loaded.ShouldContain(p => p.Name == "PdkExternal" && !p.IsBundled && p.ComponentCount == 1);
    }

    [Fact]
    public async Task ReloadUserPdksAtStartupAsync_addsComponent_fromDirScannedPdk_toAllTemplates()
    {
        SeedRoot();

        await _leftPanel.ReloadUserPdksAtStartupAsync(_userPdkRoot);

        _leftPanel.AllTemplates.ShouldContain(t => t.Name == "Straight A" && t.PdkSource == "PdkA");
        _leftPanel.FilteredTemplates.ShouldContain(t => t.Name == "Straight A",
            "no process is locked, so the reloaded component must also pass the (no-op) filter");
    }

    [Fact]
    public async Task ReloadUserPdksAtStartupAsync_never_loads_a_pdk_from_the_trash_subfolder()
    {
        SeedRoot();

        await _leftPanel.ReloadUserPdksAtStartupAsync(_userPdkRoot);

        _leftPanel.PdkManager.LoadedPdks.ShouldNotContain(p => p.Name == "TrashedPdk");
        _leftPanel.AllTemplates.ShouldNotContain(t => t.Name == "Trashed Straight");
    }

    [Fact]
    public async Task ReloadUserPdksAtStartupAsync_deadPrefsPath_doesNotCrash_andIsRemovedFromPrefs()
    {
        SeedRoot();
        var deadPath = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}.json");
        _preferencesService.AddUserPdkPath(deadPath);

        await _leftPanel.ReloadUserPdksAtStartupAsync(_userPdkRoot);

        _leftPanel.PdkManager.LoadedPdks.ShouldNotContain(p => p.FilePath == deadPath);
        _preferencesService.GetUserPdkPaths().ShouldNotContain(Path.GetFullPath(deadPath));
    }

    [Fact]
    public async Task ReloadUserPdksAtStartupAsync_samePath_inDirScan_and_prefs_registersOnce()
    {
        var (aPath, _, _) = SeedRoot();
        _preferencesService.AddUserPdkPath(aPath);

        await _leftPanel.ReloadUserPdksAtStartupAsync(_userPdkRoot);

        _leftPanel.PdkManager.LoadedPdks.Count(p => p.Name == "PdkA").ShouldBe(1);
    }

    [Fact]
    public async Task ReloadUserPdksAtStartupAsync_skips_legacy_gdsImport_pdk_files_without_deleting_them()
    {
        SeedRoot();
        // A pre-#830 GDS-import PDK file: imports are design-scoped now, so the
        // stale global file must neither load at startup nor be deleted.
        Directory.CreateDirectory(_userPdkRoot);
        var legacyPath = Path.Combine(_userPdkRoot, "gds-import-chip.json");
        new PdkJsonSaver().SaveToFile(new PdkDraft
        {
            Name = "GDS Import - chip",
            Backend = "nazca",
            ProcessAgnostic = true,
            Components = new List<PdkComponentDraft> { SimpleComponent("Legacy WG") },
        }, legacyPath);

        await _leftPanel.ReloadUserPdksAtStartupAsync(_userPdkRoot);

        _leftPanel.PdkManager.LoadedPdks.ShouldNotContain(p => p.Name == "GDS Import - chip");
        _leftPanel.AllTemplates.ShouldNotContain(t => t.Name == "Legacy WG");
        File.Exists(legacyPath).ShouldBeTrue("skipped silently, never deleted");
    }

    [Fact]
    public async Task ReloadUserPdksAtStartupAsync_reappliesProcessLock_and_refiltersOnce()
    {
        SeedRoot();

        _leftPanel.ApplyActiveProcess(new ActiveProcessSelection(
            DisplayName: "Other Process", Fingerprint: null,
            MemberPdkNames: new List<string> { "Some Other PDK" }, IsPlayground: false));

        await _leftPanel.ReloadUserPdksAtStartupAsync(_userPdkRoot);

        _leftPanel.AllTemplates.ShouldContain(t => t.Name == "Straight A");
        _leftPanel.FilteredTemplates.ShouldNotContain(t => t.Name == "Straight A",
            "the active process lock must still govern a PDK reloaded at startup");
    }

    [Fact]
    public async Task ReloadUserPdksAtStartupAsync_respectsPersistedUncheck_butKeepsUnknownPdkEnabled()
    {
        SeedRoot();
        var store = new UserPdkStore(_userPdkRoot, new PdkJsonSaver(), new PdkLoader());
        store.CreateNamedPdkWithProcess("PdkC", SimpleProcess("Process C"), "nazca", null);

        _preferencesService.SetPdkFilterState(
            enabledPdkNames: new[] { "PdkA" },
            knownPdkNames: new[] { "PdkA", "PdkB" });

        await _leftPanel.ReloadUserPdksAtStartupAsync(_userPdkRoot);

        _leftPanel.PdkManager.LoadedPdks.Single(p => p.Name == "PdkA").IsEnabled.ShouldBeTrue();
        _leftPanel.PdkManager.LoadedPdks.Single(p => p.Name == "PdkB").IsEnabled.ShouldBeFalse(
            "a deliberately unchecked PDK must stay unchecked across restarts");
        _leftPanel.PdkManager.LoadedPdks.Single(p => p.Name == "PdkC").IsEnabled.ShouldBeTrue(
            "a PDK the last save never saw must not be treated as deliberately unchecked");
    }
}
