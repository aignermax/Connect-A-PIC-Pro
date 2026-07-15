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

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
