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

/// <summary>
/// Covers <see cref="LeftPanelViewModel.ReloadUserPdksAtStartupAsync"/> (issue #700): user-authored
/// PDKs are found on disk (directory scan of the user-pdks root + remembered import paths from
/// <see cref="UserPreferencesService.GetUserPdkPaths"/>) but nothing replays them at app start, so
/// they vanish from the PDK-management list on the next launch even though the "New Component"
/// dialog (which scans the directory directly) still sees them. These tests build a
/// <see cref="LeftPanelViewModel"/> the same way <see cref="LeftPanelNewComponentTests"/> does
/// (no bundled-PDK <c>Initialize()</c> call, so assertions are not muddied by real bundled PDKs)
/// and drive the reload against a temp root + a fake preferences file.
/// </summary>
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
                try { File.Delete(path); } catch { /* best effort */ }
            }
        }
        foreach (var dir in new[] { _userPdkRoot, _externalDir })
        {
            if (Directory.Exists(dir))
            {
                try { Directory.Delete(dir, true); } catch { /* best effort */ }
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

    /// <summary>Writes the three root-level fixtures (a-with-component, b-empty, .trash) and returns their paths.</summary>
    private (string aPath, string bPath, string trashPath) SeedRoot()
    {
        var store = new UserPdkStore(_userPdkRoot, new PdkJsonSaver(), new PdkLoader());
        var aPath = store.SaveToNamedPdk("PdkA", SimpleProcess("Process A"), SimpleComponent("Straight A"), "nazca", null);
        var bPath = store.CreateNamedPdkWithProcess("PdkB", SimpleProcess("Process B"), "nazca", null);

        // A trashed file living directly under user-pdks/.trash/ must never be picked up by the
        // startup reload — Directory.GetFiles(root, "*.json") with the default TopDirectoryOnly
        // scope already excludes it, but the fixture proves that in practice.
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

        // Must not throw.
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
    public async Task ReloadUserPdksAtStartupAsync_reappliesProcessLock_and_refiltersOnce()
    {
        SeedRoot();

        // Lock the library to a process that does NOT include PdkA, so the reload's mandatory
        // ReapplyActiveProcessAfterPdkChange() + FilterComponents() call is actually observable:
        // the reloaded component must stay excluded from FilteredTemplates.
        _leftPanel.ApplyActiveProcess(new ActiveProcessSelection(
            DisplayName: "Other Process", Fingerprint: null,
            MemberPdkNames: new List<string> { "Some Other PDK" }, IsPlayground: false));

        await _leftPanel.ReloadUserPdksAtStartupAsync(_userPdkRoot);

        _leftPanel.AllTemplates.ShouldContain(t => t.Name == "Straight A");
        _leftPanel.FilteredTemplates.ShouldNotContain(t => t.Name == "Straight A",
            "the active process lock must still govern a PDK reloaded at startup");
    }

    /// <summary>
    /// PR #739 review, both directions: a user PDK that was deliberately unchecked (known to the
    /// last save, absent from the enabled set) must come back unchecked after the startup reload —
    /// while a PDK the save never saw (e.g. created under a process lock, where the filter state
    /// is not persisted) must keep its default enabled state instead of being treated as
    /// deliberately unchecked.
    /// </summary>
    [Fact]
    public async Task ReloadUserPdksAtStartupAsync_respectsPersistedUncheck_butKeepsUnknownPdkEnabled()
    {
        SeedRoot(); // PdkA (with component) + PdkB (empty)
        var store = new UserPdkStore(_userPdkRoot, new PdkJsonSaver(), new PdkLoader());
        store.CreateNamedPdkWithProcess("PdkC", SimpleProcess("Process C"), "nazca", null);

        // Last session: PdkA+PdkB were loaded, the user unchecked PdkB; PdkC did not exist yet.
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
