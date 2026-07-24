using System;
using System.IO;
using System.Linq;
using CAP.Avalonia.Services;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

public class PdkFirstEndToEndTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lunima-pdkfirst-e2e-" + Guid.NewGuid().ToString("N"));

    private UserPdkStore Store() => new(_root, new PdkJsonSaver(), new PdkLoader());

    private static ProcessDefinition Proc(string n) => new() { Name = n };

    private static PdkComponentDraft Comp(string n) => new()
    {
        Name = n,
        WidthMicrometers = 10,
        HeightMicrometers = 2,
        RawCode = "import gdsfactory as gf\ncomponent = gf.components.straight()",
        RawCodeBackend = "gdsfactory",
        Pins = new() { new PhysicalPinDraft { Name = "o1" }, new PhysicalPinDraft { Name = "o2" } }
    };

    [Fact]
    public void SaveToNamedPdk_then_ListCustomPdks_shows_named_pdk_with_its_process()
    {
        var store = Store();
        store.SaveToNamedPdk("My Lib", Proc("P"), Comp("x"), "gdsfactory", null);

        var list = store.ListCustomPdks();

        list.ShouldContain(i => i.Name == "My Lib" && i.Process.Name == "P");
    }

    [Fact]
    public void AppendToExistingPdk_results_in_two_components_in_the_saved_file()
    {
        var store = Store();
        var path = store.SaveToNamedPdk("My Lib", Proc("P"), Comp("x"), "gdsfactory", null);

        store.AppendToExistingPdk(path, Comp("y"));

        var pdk = new PdkLoader().LoadFromFileForEditing(path);
        pdk.Components.Count.ShouldBe(2);
    }

    [Fact]
    public void ConvertToTemplate_of_a_reloaded_rawcode_component_carries_rawcode_onto_the_template()
    {
        var store = Store();
        var path = store.SaveToNamedPdk("My Lib", Proc("P"), Comp("x"), "gdsfactory", null);
        var pdk = new PdkLoader().LoadFromFileForEditing(path);
        var loadedComponent = pdk.Components.Single(c => c.Name == "x");

        var template = PdkTemplateConverter.ConvertToTemplate(loadedComponent, "My Lib", null);

        template.RawCode.ShouldContain("gf.components.straight");
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
