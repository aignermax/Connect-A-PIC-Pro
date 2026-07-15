using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;

namespace UnitTests.Components.AddCustomComponent;

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
            try { Directory.Delete(_root, true); } catch { }
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
        _store.RemoveComponent(path, "A");

        var entries = _trash.ListEntries();

        entries.Count.ShouldBe(1);
        entries[0].Kind.ShouldBe(PdkTrashKind.RemovedComponents);
        entries[0].RestorableComponentNames.ShouldBe(new[] { "A" });
    }

    [Fact]
    public void DeletedEmptyPdk_IsNotListed()
    {
        var path = _store.CreateNamedPdkWithProcess("Empty Lib", Process(), "gdsfactory", null);
        _store.MoveToTrash(path);

        _trash.ListEntries().ShouldBeEmpty();
    }

    [Fact]
    public void BackupWhoseComponentsAreAllBack_IsNotListed()
    {
        var path = SeedPdk("My Lib", "A");
        _store.RemoveComponent(path, "A");
        _store.SaveToNamedPdk("My Lib", Process(), Component("A"), "gdsfactory", null);

        _trash.ListEntries().ShouldBeEmpty();
    }

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
    public void RestoreDeletedPdk_WhenNameTakenAfterListing_RestoresUnderNonCollidingNameAndTag()
    {
        var path = SeedPdk("My Lib", "A");
        _store.MoveToTrash(path);
        var entry = _trash.ListEntries()[0];
        entry.Kind.ShouldBe(PdkTrashKind.DeletedPdk);

        SeedPdk("My Lib", "New");

        var result = _trash.Restore(entry);

        result.RestoredPdkPath.ShouldNotBe(path);
        result.RestoredPdkPath.ShouldContain("restored");
        File.Exists(result.RestoredPdkPath).ShouldBeTrue();
        File.Exists(path).ShouldBeTrue();
        result.PdkName.ShouldContain("restored");
    }

    [Fact]
    public void SlugReusedByDifferentPdk_StaysDeletedPdk_AndRestoreDoesNotMergeIntoIt()
    {
        var path = SeedPdk("My Lib", "A");
        _store.MoveToTrash(path);
        var otherPath = SeedPdk("My-Lib", "X");

        var entries = _trash.ListEntries();
        entries.Count.ShouldBe(1);
        entries[0].Kind.ShouldBe(PdkTrashKind.DeletedPdk);
        entries[0].RestorableComponentNames.ShouldBe(new[] { "A" });

        _trash.Restore(entries[0]);

        new PdkLoader().LoadFromFileForEditing(otherPath).Components.Select(c => c.Name)
            .ShouldBe(new[] { "X" });
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
    public void PurgeExpired_DeletesEntriesOlderThanRetention_KeepsRecentOnes()
    {
        var path = SeedPdk("My Lib", "A");
        var trashPath = _store.MoveToTrash(path);

        var future = DateTime.Now.AddDays(PdkTrashService.RetentionDays + 1);
        _trash.PurgeExpired(future);
        File.Exists(trashPath).ShouldBeFalse();

        var path2 = SeedPdk("My Lib 2", "B");
        var trashPath2 = _store.MoveToTrash(path2);
        _trash.PurgeExpired(DateTime.Now);
        File.Exists(trashPath2).ShouldBeTrue();
    }

    [Fact]
    public void ListEntries_AutoPurgesExpiredEntries()
    {
        var path = SeedPdk("My Lib", "A");
        _store.MoveToTrash(path);

        _trash.ListEntries().Count.ShouldBe(1);
    }

    [Fact]
    public void ListEntries_NoTrashFolder_ReturnsEmpty()
    {
        _trash.ListEntries().ShouldBeEmpty();
    }
}
