using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CAP.Avalonia.Services;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

public class RawCodeEndToEndTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lunima-rawcode-e2e-" + Guid.NewGuid().ToString("N"));

    private static PdkComponentDraft BuildDraft() => new()
    {
        Name = "My Cell",
        WidthMicrometers = 10,
        HeightMicrometers = 2,
        RawCode = "import gdsfactory as gf\ncomponent = gf.components.straight()",
        RawCodeBackend = "gdsfactory",
        Pins = new List<PhysicalPinDraft>
        {
            new() { Name = "o1", OffsetXMicrometers = 0, OffsetYMicrometers = 1, AngleDegrees = 180 },
            new() { Name = "o2", OffsetXMicrometers = 10, OffsetYMicrometers = 1, AngleDegrees = 0 }
        }
    };

    [Fact]
    public void RawCode_component_survives_draft_to_template()
    {
        var draft = BuildDraft();

        var store = new UserPdkStore(_root, new PdkJsonSaver(), new PdkLoader());
        var path = store.Save(new ProcessDefinition { Name = "My P" }, draft, "gdsfactory", null);
        var reloaded = new PdkLoader().LoadFromFileForEditing(path);
        var reloadedComponent = reloaded.Components.Single(c => c.Name == "My Cell");
        reloadedComponent.RawCode.ShouldContain("gf.components.straight");

        var template = PdkTemplateConverter.ConvertToTemplate(reloadedComponent, "My P", null);
        template.RawCode.ShouldContain("gf.components.straight");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
