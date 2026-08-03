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

    // ── SaveToProcessAgnosticNamedPdk ────────────────────────────────────────

    [Fact]
    public void SaveToProcessAgnosticNamedPdk_replaces_component_case_insensitively()
    {
        var store = CreateStore();
        store.SaveToProcessAgnosticNamedPdk("Lib", Comp("WG"), "nazca");
        var path = store.SaveToProcessAgnosticNamedPdk("Lib", Comp("wg"), "nazca");

        var reloaded = new PdkLoader().LoadFromFileForEditing(path);
        reloaded.Components.ShouldHaveSingleItem().Name.ShouldBe("wg",
            "same name in different case replaces, never duplicates");
    }

    [Fact]
    public void SaveToProcessAgnosticNamedPdk_twice_same_component_replaces_not_duplicates()
    {
        var store = CreateStore();
        store.SaveToProcessAgnosticNamedPdk("Lib", Comp("X"), "nazca");
        var path = store.SaveToProcessAgnosticNamedPdk("Lib", Comp("X"), "nazca");

        new PdkLoader().LoadFromFileForEditing(path).Components.ShouldHaveSingleItem();
    }

    [Fact]
    public void SaveToProcessAgnosticNamedPdk_flips_existing_file_to_process_agnostic()
    {
        var store = CreateStore();
        // A process-bound file at the same slug target: the GDS-import save
        // path must mark it process-agnostic without losing its components.
        var path = store.SaveToNamedPdk("Lib", Process("P"), Comp("A"), "gdsfactory", null);

        store.SaveToProcessAgnosticNamedPdk("Lib", Comp("B"), "nazca").ShouldBe(path);

        var reloaded = new PdkLoader().LoadFromFileForEditing(path);
        reloaded.ProcessAgnostic.ShouldBeTrue();
        reloaded.Components.Select(c => c.Name).ShouldBe(new[] { "A", "B" });
    }

    [Fact]
    public void SaveToProcessAgnosticNamedPdk_invalid_existing_pdk_throws_friendly_error()
    {
        // Hand-edited into a validation-failing state (blank pin name): the
        // raw PdkValidationException must not escape mid-import.
        var store = CreateStore();
        var path = store.ResolveNamedPath("Lib");
        Directory.CreateDirectory(_root);
        File.WriteAllText(path,
            "{\"name\":\"Lib\",\"components\":[{\"name\":\"Bad\",\"widthMicrometers\":10," +
            "\"heightMicrometers\":5,\"pins\":[{\"name\":\"\",\"offsetXMicrometers\":0," +
            "\"offsetYMicrometers\":0,\"angleDegrees\":0}]}]}");

        var ex = Should.Throw<InvalidDataException>(
            () => store.SaveToProcessAgnosticNamedPdk("Lib", Comp("X"), "nazca"));
        ex.Message.ShouldContain(path);
        ex.Message.ShouldContain("import was aborted");
        ex.InnerException.ShouldBeOfType<PdkValidationException>();
    }

    [Fact]
    public void SaveToProcessAgnosticNamedPdk_corrupt_json_throws_friendly_error()
    {
        var store = CreateStore();
        var path = store.ResolveNamedPath("Lib");
        Directory.CreateDirectory(_root);
        File.WriteAllText(path, "{ this is not json");

        var ex = Should.Throw<InvalidDataException>(
            () => store.SaveToProcessAgnosticNamedPdk("Lib", Comp("X"), "nazca"));
        ex.Message.ShouldContain(path);
        ex.Message.ShouldContain("import was aborted");
    }

    // ── ResolveAvailablePdkName ──────────────────────────────────────────────

    [Fact]
    public void ResolveAvailablePdkName_no_existing_file_returns_desired_name() =>
        CreateStore().ResolveAvailablePdkName("My Lib").ShouldBe("My Lib");

    [Fact]
    public void ResolveAvailablePdkName_same_pdk_returns_desired_name()
    {
        var store = CreateStore();
        store.SaveToProcessAgnosticNamedPdk("My Lib", Comp("X"), "nazca");

        store.ResolveAvailablePdkName("My Lib").ShouldBe("My Lib",
            "a re-import of the same PDK merges into its own file");
    }

    [Fact]
    public void ResolveAvailablePdkName_slug_collision_with_different_pdk_suffixes_deterministically()
    {
        var store = CreateStore();
        store.SaveToProcessAgnosticNamedPdk("GDS Import - my circuit", Comp("X"), "nazca");

        // Different name, same slug (gds-import-my-circuit.json) → suffixed.
        store.ResolveAvailablePdkName("GDS Import - my-circuit").ShouldBe("GDS Import - my-circuit-2");

        // When the -2 slug target is ALSO occupied by a different PDK, the loop
        // moves on to -3. (A -2 file holding "GDS Import - my-circuit-2" itself
        // would be its own PDK — that merges, see the same-name test above.)
        new PdkJsonSaver().SaveToFile(
            new PdkDraft { Name = "Unrelated PDK", Backend = "nazca", Components = new() },
            store.ResolveNamedPath("GDS Import - my-circuit-2"));
        store.ResolveAvailablePdkName("GDS Import - my-circuit").ShouldBe("GDS Import - my-circuit-3");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
