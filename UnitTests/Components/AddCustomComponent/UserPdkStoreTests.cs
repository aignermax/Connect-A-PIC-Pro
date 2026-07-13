using System;
using System.Collections.Generic;
using System.IO;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

/// <summary>
/// Verifies <see cref="UserPdkStore"/>: user-authored components are persisted into a
/// per-process, per-user file under a writable root — never into the bundled foundry
/// PDK JSONs — with save-then-reload roundtripping and replace-not-duplicate semantics.
/// </summary>
public class UserPdkStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lunima-userpdk-" + Guid.NewGuid().ToString("N"));

    private UserPdkStore CreateStore() => new(_root, new PdkJsonSaver(), new PdkLoader());

    private static ProcessDefinition Process(string name) => new() { Name = name };

    // Width/height and two pins are required: PdkLoader.LoadFromFileForEditing still
    // structurally validates components (name/dimensions/pins) even though it tolerates
    // a missing NazcaOriginOffset. GdsFactoryFunction marks this as a gdsfactory-backend
    // component, exempt from the Nazca-only origin-offset requirement.
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
    public void AppendToExistingPdk_withReplacesName_removesTheRenamedFromEntry()
    {
        var store = CreateStore();
        var path = store.CreateNamedPdkWithProcess("Lib", Process("P"), "gdsfactory", null);
        store.AppendToExistingPdk(path, Comp("Old"));
        store.AppendToExistingPdk(path, Comp("Untouched"));

        store.AppendToExistingPdk(path, Comp("New"), replacesName: "old"); // case-insensitive

        var reloaded = new PdkLoader().LoadFromFileForEditing(path);
        reloaded.Components.FindAll(c => c.Name == "Old").Count.ShouldBe(0); // no orphan (#730)
        reloaded.Components.FindAll(c => c.Name == "New").Count.ShouldBe(1);
        reloaded.Components.FindAll(c => c.Name == "Untouched").Count.ShouldBe(1);
    }

    [Fact]
    public void AppendToExistingPdk_withoutReplacesName_keepsOtherComponents()
    {
        var store = CreateStore();
        var path = store.CreateNamedPdkWithProcess("Lib", Process("P"), "gdsfactory", null);
        store.AppendToExistingPdk(path, Comp("A"));

        store.AppendToExistingPdk(path, Comp("B"));

        var reloaded = new PdkLoader().LoadFromFileForEditing(path);
        reloaded.Components.Count.ShouldBe(2);
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
