using CAP.Avalonia.Services;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

/// <summary>
/// Verifies that a raw-code component definition (issue #702 follow-up: rawcode
/// authoring) survives the PDK JSON save/load round-trip and is carried over
/// onto the <see cref="CAP.Avalonia.ViewModels.Library.ComponentTemplate"/>
/// produced by <see cref="PdkTemplateConverter.ConvertToTemplate"/>.
/// </summary>
public class RawCodePersistenceTests
{
    [Fact]
    public void PdkComponentDraft_roundtrips_rawcode_through_saver_and_loader()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lunima-rawcode-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, "p.json");
        var pdk = new PdkDraft
        {
            Name = "My P", Backend = "gdsfactory", Components = new()
            {
                new PdkComponentDraft
                {
                    Name = "My Cell", WidthMicrometers = 10, HeightMicrometers = 2,
                    RawCode = "import gdsfactory as gf\ncomponent = gf.components.straight()",
                    RawCodeBackend = "gdsfactory",
                    Pins = new() { new PhysicalPinDraft { Name = "o1" }, new PhysicalPinDraft { Name = "o2" } }
                }
            }
        };
        new PdkJsonSaver().SaveToFile(pdk, path);
        var reloaded = new PdkLoader().LoadFromFileForEditing(path);
        var c = reloaded.Components[0];
        c.RawCode.ShouldContain("gf.components.straight");
        c.RawCodeBackend.ShouldBe("gdsfactory");
        System.IO.Directory.Delete(dir, true);
    }

    [Fact]
    public void ConvertToTemplate_carries_rawcode_onto_the_template()
    {
        var draft = new PdkComponentDraft
        {
            Name = "My Cell", WidthMicrometers = 10, HeightMicrometers = 2,
            RawCode = "component = gf.components.straight()", RawCodeBackend = "gdsfactory",
            Pins = new() { new PhysicalPinDraft { Name = "o1" }, new PhysicalPinDraft { Name = "o2" } }
        };
        var template = PdkTemplateConverter.ConvertToTemplate(draft, "My P", null);
        template.RawCode.ShouldBe("component = gf.components.straight()");
        template.RawCodeBackend.ShouldBe("gdsfactory");
    }
}
