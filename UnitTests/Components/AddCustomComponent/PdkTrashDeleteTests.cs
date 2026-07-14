using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.ViewModels.Canvas;
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

/// <summary>
/// Covers LC-T5 (delete-to-trash): <see cref="UserPdkStore.MoveToTrash"/> and
/// <see cref="UserPdkStore.RemoveComponent"/> at the store level, and
/// <see cref="LeftPanelViewModel.UnregisterPdk"/> /
/// <see cref="LeftPanelViewModel.RemoveCustomComponentCommand"/> at the library level — the
/// mirror image of <see cref="RegisterCreatedPdkTests"/> / <see cref="LibraryEditActionTests"/>.
/// Never touches bundled (Foundry) PDKs; only custom, user-authored ones.
/// </summary>
public class PdkTrashDeleteTests : IDisposable
{
    private readonly List<string> _tempDirs = new();
    private readonly List<string> _tempFiles = new();

    private UserPdkStore CreateStore()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lunima-trash-{Guid.NewGuid():N}");
        _tempDirs.Add(root);
        return new UserPdkStore(root, new PdkJsonSaver(), new PdkLoader());
    }

    /// <summary>Builds a <see cref="LeftPanelViewModel"/>, optionally with the "add custom component" collaborators wired (mirrors <c>LibraryEditActionTests</c>).</summary>
    private LeftPanelViewModel CreateLeftPanelViewModel(UserPdkStore? userPdkStore, out UserPreferencesService preferences)
    {
        var canvas = new DesignCanvasViewModel();
        var libraryManager = new GroupLibraryManager();
        var pdkLoader = new PdkLoader();
        var preferencesPath = Path.Combine(Path.GetTempPath(), $"test-preferences-{Guid.NewGuid()}.json");
        _tempFiles.Add(preferencesPath);
        preferences = new UserPreferencesService(preferencesPath);

        AddCustomComponentDependencies? deps = null;
        if (userPdkStore != null)
        {
            var nazca = new Mock<IComponentPreviewRenderer>();
            var gds = new Mock<IComponentPreviewRenderer>();
            var extractor = new ComponentGeometryExtractor(nazca.Object, gds.Object);
            deps = new AddCustomComponentDependencies(extractor, Fdtd: null, UserPdkStore: userPdkStore);
        }

        return new LeftPanelViewModel(
            canvas, libraryManager, pdkLoader, preferences,
            new HierarchyPanelViewModel(canvas),
            new PdkManagerViewModel(),
            new ComponentLibraryViewModel(libraryManager),
            addCustomComponentDeps: deps);
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

    public void Dispose()
    {
        foreach (var dir in _tempDirs.Where(Directory.Exists))
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
        foreach (var file in _tempFiles.Where(File.Exists))
        {
            try { File.Delete(file); } catch { /* best effort */ }
        }
    }

    // ---------- UserPdkStore.MoveToTrash ----------

    [Fact]
    public void MoveToTrash_movesFileIntoTrashSubfolder_andRemovesOriginal()
    {
        var store = CreateStore();
        var path = store.CreateNamedPdkWithProcess("My Lib", SimpleProcess("P"), "gdsfactory", null);

        var trashedPath = store.MoveToTrash(path);

        File.Exists(path).ShouldBeFalse();
        File.Exists(trashedPath).ShouldBeTrue();
        Path.GetDirectoryName(trashedPath)!.ShouldEndWith(".trash");
        Path.GetFileName(trashedPath).ShouldStartWith("my-lib-");
    }

    [Fact]
    public void MoveToTrash_preservesFileContent()
    {
        var store = CreateStore();
        var path = store.SaveToNamedPdk("My Lib", SimpleProcess("P"), SimpleComponent("Straight A"), "nazca", null);

        var trashedPath = store.MoveToTrash(path);

        var reloaded = new PdkLoader().LoadFromFileForEditing(trashedPath);
        reloaded.Name.ShouldBe("My Lib");
        reloaded.Components.ShouldContain(c => c.Name == "Straight A");
    }

    [Fact]
    public void MoveToTrash_nonexistentFile_throwsFileNotFoundException()
    {
        var store = CreateStore();
        var deadPath = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}.json");

        Should.Throw<FileNotFoundException>(() => store.MoveToTrash(deadPath));
    }

    [Fact]
    public void MoveToTrash_twiceForSameBaseName_neverOverwritesTheEarlierTrashedCopy()
    {
        var store = CreateStore();
        var root = Path.Combine(Path.GetTempPath(), $"reuse-root-{Guid.NewGuid():N}");
        _tempDirs.Add(root);
        var reuseStore = new UserPdkStore(root, new PdkJsonSaver(), new PdkLoader());

        var pathFirst = reuseStore.CreateNamedPdkWithProcess("Dup", SimpleProcess("P"), "gdsfactory", null);
        reuseStore.MoveToTrash(pathFirst);

        // A second PDK with the exact same slug ("dup.json"), trashed right after — must not
        // collide with (or silently overwrite) the first trashed copy.
        var pathSecond = reuseStore.CreateNamedPdkWithProcess("Dup", SimpleProcess("P"), "gdsfactory", null);
        reuseStore.MoveToTrash(pathSecond);

        var trashDir = Path.Combine(root, ".trash");
        var trashedFiles = Directory.GetFiles(trashDir, "dup-*.json");
        trashedFiles.Length.ShouldBe(2, "both trashed copies must survive, never overwriting one another");
    }

    // ---------- UserPdkStore.RemoveComponent ----------

    [Fact]
    public void RemoveComponent_removesNamedComponent_rewritesFileWithoutIt()
    {
        var store = CreateStore();
        var path = store.SaveToNamedPdk("My Lib", SimpleProcess("P"), SimpleComponent("Straight A"), "nazca", null);
        store.AppendToExistingPdk(path, SimpleComponent("Straight B"));

        var result = store.RemoveComponent(path, "Straight A");

        result.ShouldBe(path);
        var reloaded = new PdkLoader().LoadFromFileForEditing(path);
        reloaded.Components.ShouldNotContain(c => c.Name == "Straight A");
        reloaded.Components.ShouldContain(c => c.Name == "Straight B");
    }

    [Fact]
    public void RemoveComponent_matchesCaseInsensitively()
    {
        var store = CreateStore();
        var path = store.SaveToNamedPdk("My Lib", SimpleProcess("P"), SimpleComponent("Straight A"), "nazca", null);

        var result = store.RemoveComponent(path, "STRAIGHT a");

        result.ShouldBe(path);
        var reloaded = new PdkLoader().LoadFromFileForEditing(path);
        reloaded.Components.ShouldBeEmpty();
    }

    [Fact]
    public void RemoveComponent_backsUpPreEditFile_toTrash_byDefault()
    {
        var store = CreateStore();
        var root = Path.Combine(Path.GetTempPath(), $"backup-root-{Guid.NewGuid():N}");
        _tempDirs.Add(root);
        var scoped = new UserPdkStore(root, new PdkJsonSaver(), new PdkLoader());
        var path = scoped.SaveToNamedPdk("My Lib", SimpleProcess("P"), SimpleComponent("Straight A"), "nazca", null);

        scoped.RemoveComponent(path, "Straight A");

        var trashDir = Path.Combine(root, ".trash");
        Directory.Exists(trashDir).ShouldBeTrue();
        var backups = Directory.GetFiles(trashDir, "my-lib-*.json");
        backups.Length.ShouldBe(1);
        var backedUp = new PdkLoader().LoadFromFileForEditing(backups[0]);
        backedUp.Components.ShouldContain(c => c.Name == "Straight A",
            "the backup must hold the PRE-edit state, including the removed component");
    }

    [Fact]
    public void RemoveComponent_backupFirstFalse_skipsBackup()
    {
        var store = CreateStore();
        var root = Path.Combine(Path.GetTempPath(), $"nobackup-root-{Guid.NewGuid():N}");
        _tempDirs.Add(root);
        var scoped = new UserPdkStore(root, new PdkJsonSaver(), new PdkLoader());
        var path = scoped.SaveToNamedPdk("My Lib", SimpleProcess("P"), SimpleComponent("Straight A"), "nazca", null);

        scoped.RemoveComponent(path, "Straight A", backupFirst: false);

        var trashDir = Path.Combine(root, ".trash");
        Directory.Exists(trashDir).ShouldBeFalse();
    }

    [Fact]
    public void RemoveComponent_nonexistentFile_returnsNull_asNoOp()
    {
        var store = CreateStore();
        var deadPath = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}.json");

        var result = store.RemoveComponent(deadPath, "Whatever");

        result.ShouldBeNull();
    }

    [Fact]
    public void RemoveComponent_componentNotPresent_returnsNull_andDoesNotBackup()
    {
        var store = CreateStore();
        var root = Path.Combine(Path.GetTempPath(), $"nomatch-root-{Guid.NewGuid():N}");
        _tempDirs.Add(root);
        var scoped = new UserPdkStore(root, new PdkJsonSaver(), new PdkLoader());
        var path = scoped.SaveToNamedPdk("My Lib", SimpleProcess("P"), SimpleComponent("Straight A"), "nazca", null);

        var result = scoped.RemoveComponent(path, "Nonexistent Component");

        result.ShouldBeNull();
        Directory.Exists(Path.Combine(root, ".trash")).ShouldBeFalse();
    }

    // ---------- LeftPanelViewModel.UnregisterPdk ----------

    [Fact]
    public void UnregisterPdk_removesTemplates_pdkEntry_draft_andPrefsPath()
    {
        var store = CreateStore();
        var path = store.SaveToNamedPdk("My Lib", SimpleProcess("P"), SimpleComponent("Straight A"), "nazca", null);
        var vm = CreateLeftPanelViewModel(store, out var preferences);
        vm.RegisterCreatedPdk(path);
        preferences.AddUserPdkPath(path);

        var removed = vm.UnregisterPdk(path);

        removed.ShouldBeTrue();
        vm.PdkManager.LoadedPdks.ShouldNotContain(p => p.Name == "My Lib");
        vm.AllTemplates.ShouldNotContain(t => t.Name == "Straight A");
        vm.GetLoadedPdkDrafts().ShouldNotContain(d => d.Name == "My Lib");
        preferences.GetUserPdkPaths().ShouldNotContain(Path.GetFullPath(path));
    }

    [Fact]
    public void UnregisterPdk_bundledPdk_isNoOp_andReturnsFalse()
    {
        var vm = CreateLeftPanelViewModel(null, out _);
        vm.PdkManager.RegisterPdk("Foundry Pdk", "/some/bundled.json", isBundled: true, componentCount: 3);

        var removed = vm.UnregisterPdk("/some/bundled.json");

        removed.ShouldBeFalse();
        vm.PdkManager.LoadedPdks.ShouldContain(p => p.Name == "Foundry Pdk");
    }

    [Fact]
    public void UnregisterPdk_pathNotLoaded_isNoOp_andReturnsFalse()
    {
        var vm = CreateLeftPanelViewModel(null, out _);

        var removed = vm.UnregisterPdk(Path.Combine(Path.GetTempPath(), $"never-loaded-{Guid.NewGuid():N}.json"));

        removed.ShouldBeFalse();
    }

    [Fact]
    public void UnregisterPdk_dropsCategory_onlyWhenNoOtherTemplateUsesIt()
    {
        var store = CreateStore();
        var pathA = store.SaveToNamedPdk("Lib A", SimpleProcess("P1"), SimpleComponent("Straight A"), "nazca", null);
        var pathB = store.SaveToNamedPdk("Lib B", SimpleProcess("P2"), SimpleComponent("Straight B"), "nazca", null);
        var vm = CreateLeftPanelViewModel(store, out _);
        vm.RegisterCreatedPdk(pathA);
        vm.RegisterCreatedPdk(pathB);
        // Both components share the "Test" category (see SimpleComponent).
        vm.Categories.ShouldContain("Test");

        vm.UnregisterPdk(pathA);

        vm.Categories.ShouldContain("Test", "Lib B's component still uses the category");
        vm.AllTemplates.ShouldContain(t => t.Name == "Straight B");

        vm.UnregisterPdk(pathB);

        vm.Categories.ShouldNotContain("Test", "no remaining template uses the category");
    }

    [Fact]
    public void UnregisterPdk_reappliesActiveProcessLock_soFilteredTemplatesStayCorrect()
    {
        var store = CreateStore();
        var pathLocked = store.SaveToNamedPdk("Locked Out", SimpleProcess("Foreign"), SimpleComponent("Straight X"), "nazca", null);
        var vm = CreateLeftPanelViewModel(store, out _);
        vm.RegisterCreatedPdk(pathLocked);
        vm.ApplyActiveProcess(new ActiveProcessSelection(
            DisplayName: "Other Process", Fingerprint: null,
            MemberPdkNames: new List<string> { "Some Other PDK" }, IsPlayground: false));
        vm.FilteredTemplates.ShouldNotContain(t => t.Name == "Straight X");

        Should.NotThrow(() => vm.UnregisterPdk(pathLocked));

        vm.FilteredTemplates.ShouldNotContain(t => t.Name == "Straight X");
        vm.AllTemplates.ShouldNotContain(t => t.Name == "Straight X");
    }

    // ---------- LeftPanelViewModel.RemoveCustomComponent ----------

    [Fact]
    public void RemoveCustomComponent_forCustomTemplate_removesFromStoreFileAndLibrary()
    {
        var store = CreateStore();
        var path = store.SaveToNamedPdk("My Lib", SimpleProcess("P"), SimpleComponent("Straight A"), "nazca", null);
        var vm = CreateLeftPanelViewModel(store, out _);
        vm.RegisterCreatedPdk(path);
        var template = vm.AllTemplates.First(t => t.Name == "Straight A");

        vm.RemoveCustomComponentCommand.Execute(template);

        vm.AllTemplates.ShouldNotContain(t => t.Name == "Straight A");
        var reloaded = new PdkLoader().LoadFromFileForEditing(path);
        reloaded.Components.ShouldNotContain(c => c.Name == "Straight A");
        vm.GetLoadedPdkDrafts().First(d => d.Name == "My Lib").Components
            .ShouldNotContain(c => c.Name == "Straight A", "the in-memory draft must match the on-disk file");
    }

    [Fact]
    public void RemoveCustomComponent_forBundledTemplate_isNoOp()
    {
        var vm = CreateLeftPanelViewModel(CreateStore(), out _);
        vm.PdkManager.RegisterPdk("Foundry Pdk", null, isBundled: true, componentCount: 1);
        var template = new ComponentTemplate { Name = "Grating Coupler", PdkSource = "Foundry Pdk" };
        vm.AllTemplates.Add(template);

        vm.RemoveCustomComponentCommand.Execute(template);

        vm.AllTemplates.ShouldContain(t => t.Name == "Grating Coupler");
    }

    [Fact]
    public void RemoveCustomComponent_keepsTheHostPdkEntry_evenAfterItsLastComponentIsRemoved()
    {
        var store = CreateStore();
        var path = store.SaveToNamedPdk("My Lib", SimpleProcess("P"), SimpleComponent("Straight A"), "nazca", null);
        var vm = CreateLeftPanelViewModel(store, out _);
        vm.RegisterCreatedPdk(path);
        var template = vm.AllTemplates.First(t => t.Name == "Straight A");

        vm.RemoveCustomComponentCommand.Execute(template);

        // Removing a single component deletes only the component, never the PDK itself
        // (UnregisterPdk is the separate, whole-PDK operation).
        vm.PdkManager.LoadedPdks.ShouldContain(p => p.Name == "My Lib");
    }
}
