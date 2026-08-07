using System;
using System.IO;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

public class UserPdkNamedStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lunima-namedpdk-" + Guid.NewGuid().ToString("N"));
    private UserPdkStore Store() => new(_root, new PdkJsonSaver(), new PdkLoader());
    private static ProcessDefinition Proc(string n) => new() { Name = n };
    private static PdkComponentDraft Comp(string n) => new()
    { Name = n, WidthMicrometers = 10, HeightMicrometers = 2,
      RawCode = "import gdsfactory as gf\ncomponent = gf.components.straight()", RawCodeBackend = "gdsfactory",
      Pins = new() { new PhysicalPinDraft { Name = "o1" }, new PhysicalPinDraft { Name = "o2" } } };

    [Fact]
    public void SaveToNamedPdk_creates_named_file_with_process_and_component()
    {
        var s = Store();
        var path = s.SaveToNamedPdk("My SiN Lib", Proc("CornerStone SiN 300"), Comp("mmi"), "gdsfactory", null);
        Path.GetFileName(path).ShouldBe("my-sin-lib.json");
        var pdk = new PdkLoader().LoadFromFileForEditing(path);
        pdk.Name.ShouldBe("My SiN Lib");
        pdk.Process!.Name.ShouldBe("CornerStone SiN 300");
        pdk.Components.ShouldContain(c => c.Name == "mmi");
    }

    [Fact]
    public void ListCustomPdks_returns_named_pdks_with_their_process()
    {
        var s = Store();
        s.SaveToNamedPdk("Lib A", Proc("P1"), Comp("x"), "gdsfactory", null);
        s.SaveToNamedPdk("Lib B", Proc("P2"), Comp("y"), "gdsfactory", null);
        var list = s.ListCustomPdks();
        list.Count.ShouldBe(2);
        list.ShouldContain(i => i.Name == "Lib A" && i.Process!.Name == "P1");
    }

    [Fact]
    public void AppendToExistingPdk_adds_without_duplicating()
    {
        var s = Store();
        var path = s.SaveToNamedPdk("Lib", Proc("P"), Comp("x"), "gdsfactory", null);
        s.AppendToExistingPdk(path, Comp("z"));
        var pdk = new PdkLoader().LoadFromFileForEditing(path);
        pdk.Components.Count.ShouldBe(2);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
