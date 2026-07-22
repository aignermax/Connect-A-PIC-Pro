using System;
using System.IO;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

public class NewPdkModalFlowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lunima-newpdkmodal-" + Guid.NewGuid().ToString("N"));
    private UserPdkStore Store() => new(_root, new PdkJsonSaver(), new PdkLoader());
    private static ProcessDefinition Proc(string n) => new() { Name = n };
    private static PdkComponentDraft Comp(string n) => new()
    {
        Name = n, WidthMicrometers = 10, HeightMicrometers = 2,
        RawCode = "import gdsfactory as gf\ncomponent = gf.components.straight()", RawCodeBackend = "gdsfactory",
        Pins = new() { new PhysicalPinDraft { Name = "o1" }, new PhysicalPinDraft { Name = "o2" } },
    };

    [Fact]
    public void CreateNamedPdkWithProcess_produces_listed_pdk_with_no_components()
    {
        var store = Store();

        var path = store.CreateNamedPdkWithProcess("Lib", Proc("P"), "gdsfactory", null);

        store.ListCustomPdks().ShouldContain(i => i.Name == "Lib");
        new PdkLoader().LoadFromFileForEditing(path).Components.ShouldBeEmpty();
    }

    [Fact]
    public void AppendToExistingPdk_after_empty_creation_adds_one_component()
    {
        var store = Store();
        var path = store.CreateNamedPdkWithProcess("Lib", Proc("P"), "gdsfactory", null);

        store.AppendToExistingPdk(path, Comp("mmi"));

        new PdkLoader().LoadFromFileForEditing(path).Components.Count.ShouldBe(1);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }
}
