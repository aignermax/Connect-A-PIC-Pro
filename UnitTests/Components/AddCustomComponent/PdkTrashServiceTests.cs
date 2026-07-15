using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;

namespace UnitTests.Components.AddCustomComponent;

/// <summary>
/// Verifies the user-PDK trash restore flow (PDK-lifecycle follow-up): listing classifies a
/// deleted PDK vs a removed-components backup by whether the live file still exists, and restore
/// is additive — a deleted PDK's file returns, removed components are re-added without clobbering
/// newer edits.
/// </summary>
public sealed class PdkTrashServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lunima-trash-" + Guid.NewGuid().ToString("N"));
    private readonly UserPdkStore _store;
    private readonly PdkTrashService _trash;

    public PdkTrashServiceTests()
    {
        _store = new UserPdkStore(_root, new PdkJsonSaver(), new PdkLoader());
        _trash = new PdkTrashService(_root, new PdkLoader(), new PdkJsonSaver());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private static ProcessDefinition Process(string name = "Demo") => new() { Name = name };

    private static PdkComponentDraft Component(string name) => new()
    {
        Name = name, WidthMicrometers = 5, HeightMicrometers = 1,
        RawCode = "import gdsfactory as gf\ncomponent = gf.components.straight()", RawCodeBackend = "gdsfactory",
        Pins = new() { new PhysicalPinDraft { Name = "o1" }, new PhysicalPinDraft { Name = "o2" } },
    };

    private string SeedPdk(string pdkName, params string[] componentNames)
    {
        string path = "";
        foreach (var c in componentNames)
            path = _store.SaveToNamedPdk(pdkName, Process(), Component(c), "gdsfactory", null);
        return path;
    }

    // ── listing / classification ────────────────────────────────────────────

    [Fact]
    public void DeletedPdk_IsListedAsDeletedPdk_WithAllComponents()
    {
        var path = SeedPdk("My Lib", "A", "B");
        _store.MoveToTrash(path);

        var entries = _trash.ListEntries();

        entries.Count.ShouldBe(1);
        entries[0].Kind.ShouldBe(PdkTrashKind.DeletedPdk);
        entries[0].PdkName.ShouldBe("My Lib");
        entries[0].RestorableComponentNames.ShouldBe(new[] { "A", "B" }, ignoreOrder: true);
    }

    [Fact]
    public void RemovedComponent_IsListedAsRemovedComponents_WithOnlyMissingOnes()
    {
        var path = SeedPdk("My Lib", "A", "B");
        _store.RemoveComponent(path, "A"); // backs up the pre-edit file, removes A from live

        var entries = _trash.ListEntries();

        entries.Count.ShouldBe(1);
        entries[0].Kind.ShouldBe(PdkTrashKind.RemovedComponents);
        entries[0].RestorableComponentNames.ShouldBe(new[] { "A" }); // B is still live
    }

    [Fact]
    public void DeletedEmptyPdk_IsNotListed()
    {
        // Creating a PDK, then deleting it before adding any component, leaves an empty
        // (0-component) file in trash. It has nothing worth restoring and must not show up as a
        // confusing "0 components" entry next to a same-named real PDK (field-test #741).
        var path = _store.CreateNamedPdkWithProcess("Empty Lib", Process(), "gdsfactory", null);
        _store.MoveToTrash(path);

        _trash.ListEntries().ShouldBeEmpty();
    }

    [Fact]
    public void BackupWhoseComponentsAreAllBack_IsNotListed()
    {
        var path = SeedPdk("My Lib", "A");
        _store.RemoveComponent(path, "A");          // backup contains A; live now empty
        _store.SaveToNamedPdk("My Lib", Process(), Component("A"), "gdsfactory", null); // A is back

        _trash.ListEntries().ShouldBeEmpty(); // nothing restorable
    }

    // ── restore ───────────────────────────────────────────────────────────────

    [Fact]
    public void RestoreDeletedPdk_MovesFileBack_AndReturnsComponents()
    {
        var path = SeedPdk("My Lib", "A", "B");
        _store.MoveToTrash(path);
        File.Exists(path).ShouldBeFalse();

        var result = _trash.Restore(_trash.ListEntries()[0]);

        result.Kind.ShouldBe(PdkTrashKind.DeletedPdk);
        result.RestoredPdkPath.ShouldBe(path);
        File.Exists(path).ShouldBeTrue();
        result.RestoredComponents.Select(c => c.Name).ShouldBe(new[] { "A", "B" }, ignoreOrder: true);
        _trash.ListEntries().ShouldBeEmpty();
    }

    [Fact]
    public void RestoreDeletedPdk_WhenNameTakenAfterListing_RestoresUnderNonCollidingName()
    {
        var path = SeedPdk("My Lib", "A");
        _store.MoveToTrash(path);
        var entry = _trash.ListEntries()[0];  // classified DeletedPdk while the live file is gone
        entry.Kind.ShouldBe(PdkTrashKind.DeletedPdk);

        SeedPdk("My Lib", "New"); // a fresh PDK occupies the original path AFTER listing (race guard)

        var result = _trash.Restore(entry);

        result.RestoredPdkPath.ShouldNotBe(path);
        result.RestoredPdkPath.ShouldContain("restored");
        File.Exists(result.RestoredPdkPath).ShouldBeTrue();
        File.Exists(path).ShouldBeTrue(); // the fresh PDK is untouched
    }

    [Fact]
    public void RestoreRemovedComponent_ReaddsToLiveFile()
    {
        var path = SeedPdk("My Lib", "A", "B");
        _store.RemoveComponent(path, "A");
        new PdkLoader().LoadFromFileForEditing(path).Components.Select(c => c.Name).ShouldNotContain("A");

        var result = _trash.Restore(_trash.ListEntries()[0]);

        result.Kind.ShouldBe(PdkTrashKind.RemovedComponents);
        result.RestoredComponents.Select(c => c.Name).ShouldBe(new[] { "A" });
        new PdkLoader().LoadFromFileForEditing(path).Components.Select(c => c.Name)
            .ShouldBe(new[] { "A", "B" }, ignoreOrder: true);
    }

    [Fact]
    public void Purge_RemovesTheTrashFile()
    {
        var path = SeedPdk("My Lib", "A");
        _store.MoveToTrash(path);
        var entry = _trash.ListEntries()[0];

        _trash.Purge(entry);

        File.Exists(entry.TrashFilePath).ShouldBeFalse();
        _trash.ListEntries().ShouldBeEmpty();
    }

    [Fact]
    public void ListEntries_NoTrashFolder_ReturnsEmpty()
    {
        _trash.ListEntries().ShouldBeEmpty();
    }
}
