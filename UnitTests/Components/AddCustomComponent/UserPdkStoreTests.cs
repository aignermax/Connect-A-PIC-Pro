using System;
using System.Collections.Generic;
using System.IO;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

public class UserPdkStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lunima-userpdk-" + Guid.NewGuid().ToString("N"));

    private UserPdkStore CreateStore() => new(_root, new PdkJsonSaver(), new PdkLoader());

    private static ProcessDefinition Process(string name) => new() { Name = name };

    private static PdkComponentDraft Comp(string name) => new()
    {
        Name = name,
        GdsFactoryFunction = "cspdk.sin300.coupler",
        WidthMicrometers = 10,
        HeightMicrometers = 5,
        Pins = new List<PhysicalPinDraft>
        {
            new() { Name = "in0", OffsetXMicrometers = 0, OffsetYMicrometers = 2.5, AngleDegrees = 180 },
            new() { Name = "out0", OffsetXMicrometers = 10, OffsetYMicrometers = 2.5, AngleDegrees = 0 }
        }
    };

    [Fact]
    public void ForkBundledPdk_copies_into_user_root_leaves_original_and_is_idempotent()
    {
        var bundledDir = Path.Combine(Path.GetTempPath(), "lunima-bundled-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bundledDir);
        var bundledPath = new UserPdkStore(bundledDir, new PdkJsonSaver(), new PdkLoader())
            .SaveToNamedPdk("Shipped Lib", Process("P"), Comp("A"), "gdsfactory", null);

        var store = CreateStore();
        var forked = store.ForkBundledPdk(bundledPath, "Shipped Lib");

        forked.StartsWith(_root, StringComparison.Ordinal).ShouldBeTrue();
        File.Exists(bundledPath).ShouldBeTrue();
        new PdkLoader().LoadFromFileForEditing(forked).Components.Count.ShouldBe(1);

        store.AppendToExistingPdk(forked, Comp("Edited"));
        store.ForkBundledPdk(bundledPath, "Shipped Lib").ShouldBe(forked);
        new PdkLoader().LoadFromFileForEditing(forked).Components.Select(c => c.Name).ShouldContain("Edited");

        Directory.Delete(bundledDir, true);
    }

    [Fact]
    public void SaveDraftAsFork_writes_draft_into_user_root_without_touching_source()
    {
        var store = CreateStore();
        var draft = new PdkDraft { Name = "Shipped Lib", Components = new() { Comp("A") } };

        var forkPath = store.SaveDraftAsFork(draft, "Shipped Lib");

        forkPath.StartsWith(_root, StringComparison.Ordinal).ShouldBeTrue();
        Path.GetFileName(forkPath).ShouldBe("shipped-lib.json");
        new PdkLoader().LoadFromFileForEditing(forkPath).Components.Count.ShouldBe(1);
    }

    [Fact]
    public void SaveDraftAsFork_backs_up_existing_fork_to_trash_before_replacing()
    {
        var store = CreateStore();
        var first = new PdkDraft { Name = "Shipped Lib", Components = new() { Comp("Old") } };
        var forkPath = store.SaveDraftAsFork(first, "Shipped Lib");

        var second = new PdkDraft { Name = "Shipped Lib", Components = new() { Comp("New") } };
        store.SaveDraftAsFork(second, "Shipped Lib").ShouldBe(forkPath);

        new PdkLoader().LoadFromFileForEditing(forkPath)
            .Components.Select(c => c.Name).ShouldBe(new[] { "New" });
        // The pre-existing fork state must survive in .trash — no silent data loss.
        var trashDir = Path.Combine(_root, ".trash");
        Directory.Exists(trashDir).ShouldBeTrue();
        var backup = Directory.GetFiles(trashDir, "shipped-lib-*.json").ShouldHaveSingleItem();
        new PdkLoader().LoadFromFileForEditing(backup)
            .Components.Select(c => c.Name).ShouldBe(new[] { "Old" });
    }

    [Fact]
    public void ResolvePath_is_under_user_root_and_slugified()
    {
        var store = CreateStore();
        var path = store.ResolvePath(Process("CornerStone SiN 300"));

        path.ShouldStartWith(_root);
        Path.GetFileName(path).ShouldBe("cornerstone-sin-300.json");
    }

    [Fact]
    public void Save_creates_file_and_roundtrips_the_component()
    {
        var store = CreateStore();
        var path = store.Save(Process("CornerStone SiN 300"), Comp("My Coupler"), backend: "gdsfactory", routingCrossSection: "xs_nc");

        File.Exists(path).ShouldBeTrue();
        var reloaded = new PdkLoader().LoadFromFileForEditing(path);
        reloaded.Components.ShouldContain(c => c.Name == "My Coupler");
        reloaded.Process!.Name.ShouldBe("CornerStone SiN 300");
        reloaded.Backend.ShouldBe("gdsfactory");
    }

    [Fact]
    public void Save_twice_same_name_replaces_not_duplicates()
    {
        var store = CreateStore();
        store.Save(Process("P"), Comp("X"), "gdsfactory", null);
        var path = store.Save(Process("P"), Comp("X"), "gdsfactory", null);

        var reloaded = new PdkLoader().LoadFromFileForEditing(path);
        reloaded.Components.FindAll(c => c.Name == "X").Count.ShouldBe(1);
    }

    [Fact]
    public void ComponentExists_reflects_saved_state()
    {
        var store = CreateStore();
        store.ComponentExists(Process("P"), "X").ShouldBeFalse();
        store.Save(Process("P"), Comp("X"), "gdsfactory", null);
        store.ComponentExists(Process("P"), "X").ShouldBeTrue();
    }

    /// <summary>
    /// Writes a process-agnostic PDK file into the managed root (the shape a
    /// pre-#830 GDS import produced; imports are design-scoped now).
    /// </summary>
    private string SeedProcessAgnosticPdk(UserPdkStore store, string pdkName, params PdkComponentDraft[] components)
    {
        Directory.CreateDirectory(_root);
        var path = store.ResolveNamedPath(pdkName);
        new PdkJsonSaver().SaveToFile(new PdkDraft
        {
            Name = pdkName,
            Backend = "nazca",
            ProcessAgnostic = true,
            Components = new List<PdkComponentDraft>(components),
        }, path);
        return path;
    }

    // ── ListCustomPdks ───────────────────────────────────────────────────────

    [Fact]
    public void ListCustomPdks_includes_process_agnostic_pdks_with_null_process()
    {
        // GDS-import PDKs declare no fabrication process — they must still be
        // listed as custom (user-managed) PDKs or the component editor refuses them.
        var store = CreateStore();
        var path = SeedProcessAgnosticPdk(store, "GDS Import - demo", Comp("WG"));

        var list = store.ListCustomPdks();

        var info = list.ShouldHaveSingleItem();
        info.Name.ShouldBe("GDS Import - demo");
        info.FilePath.ShouldBe(path);
        info.Process.ShouldBeNull("a process-agnostic PDK has no fabrication process, but is still user-managed");
    }

    [Fact]
    public void ListCustomPdks_still_lists_process_bound_pdks_with_their_process()
    {
        var store = CreateStore();
        store.SaveToNamedPdk("Lib", Process("P"), Comp("X"), "gdsfactory", null);
        SeedProcessAgnosticPdk(store, "GDS Import - demo", Comp("WG"));

        var list = store.ListCustomPdks();

        list.Count.ShouldBe(2);
        list.ShouldContain(i => i.Name == "Lib" && i.Process!.Name == "P");
        list.ShouldContain(i => i.Name == "GDS Import - demo" && i.Process == null);
    }

    [Fact]
    public void ListCustomPdks_skips_files_with_neither_process_nor_agnostic_flag()
    {
        // A stray file in the root that declares neither a process nor the
        // process-agnostic flag is not a managed user PDK — keep excluding it.
        var store = CreateStore();
        Directory.CreateDirectory(_root);
        File.WriteAllText(store.ResolveNamedPath("Odd"), "{\"name\":\"Odd\",\"components\":[]}");

        store.ListCustomPdks().ShouldBeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
