using System.Collections.Generic;
using System.Linq;
using CAP.Avalonia.Commands;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.LightCalculation;
using CAP_DataAccess.Persistence.PIR;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

public class RawCodePlacementSeedingTests
{
    private static ComponentTemplate BuildTemplate(string name, string? rawCode, string? backend)
    {
        return new ComponentTemplate
        {
            Name = name,
            Category = "Test",
            PdkSource = "test-pdk",
            WidthMicrometers = 10,
            HeightMicrometers = 2,
            PinDefinitions = new[]
            {
                new PinDefinition("o1", 0, 1, 180),
                new PinDefinition("o2", 10, 1, 0)
            },
            CreateSMatrix = pins =>
            {
                var allIds = pins.SelectMany(p => new[] { p.IDInFlow, p.IDOutFlow }).ToList();
                return new SMatrix(allIds, new List<(System.Guid, double)>());
            },
            RawCode = rawCode,
            RawCodeBackend = backend,
        };
    }

    [Fact]
    public void Placing_a_gdsfactory_rawcode_template_seeds_a_per_instance_override()
    {
        var template = BuildTemplate(
            "MyGfCell",
            rawCode: "import gdsfactory as gf\ncomponent = gf.components.straight()",
            backend: "gdsfactory");
        var canvas = new DesignCanvasViewModel();
        var overrides = new Dictionary<string, NazcaCodeOverride>();

        var cmd = PlaceComponentCommand.TryCreate(canvas, template, 0, 0, overrides);
        cmd.ShouldNotBeNull();
        cmd!.Execute();

        overrides.Count.ShouldBe(1);
        var ovr = overrides.Values.First();
        ovr.RawCode.ShouldContain("gf.components.straight");
        ovr.Backend.ShouldBe(OverrideBackend.GdsFactory);
    }

    [Fact]
    public void Placing_a_nazca_rawcode_template_seeds_an_override_with_nazca_backend()
    {
        var template = BuildTemplate(
            "MyNazcaCell",
            rawCode: "import nazca as nd\ncell = nd.Cell('x')",
            backend: "nazca");
        var canvas = new DesignCanvasViewModel();
        var overrides = new Dictionary<string, NazcaCodeOverride>();

        var cmd = PlaceComponentCommand.TryCreate(canvas, template, 0, 0, overrides);
        cmd!.Execute();

        overrides.Count.ShouldBe(1);
        overrides.Values.First().Backend.ShouldBe(OverrideBackend.Nazca);
    }

    [Fact]
    public void Undo_removes_the_seeded_override()
    {
        var template = BuildTemplate("MyGfCell", "component = gf.components.straight()", "gdsfactory");
        var canvas = new DesignCanvasViewModel();
        var overrides = new Dictionary<string, NazcaCodeOverride>();

        var cmd = PlaceComponentCommand.TryCreate(canvas, template, 0, 0, overrides);
        cmd!.Execute();
        overrides.Count.ShouldBe(1);

        cmd.Undo();

        overrides.Count.ShouldBe(0);
    }

    [Fact]
    public void Redo_after_undo_reseeds_the_override()
    {
        var template = BuildTemplate("MyGfCell", "component = gf.components.straight()", "gdsfactory");
        var canvas = new DesignCanvasViewModel();
        var overrides = new Dictionary<string, NazcaCodeOverride>();

        var cmd = PlaceComponentCommand.TryCreate(canvas, template, 0, 0, overrides);
        cmd!.Execute();
        cmd.Undo();
        cmd.Execute();

        overrides.Count.ShouldBe(1);
    }

    [Fact]
    public void Placing_a_non_rawcode_template_seeds_nothing()
    {
        var template = BuildTemplate("PlainCell", rawCode: null, backend: null);
        var canvas = new DesignCanvasViewModel();
        var overrides = new Dictionary<string, NazcaCodeOverride>();

        var cmd = PlaceComponentCommand.TryCreate(canvas, template, 0, 0, overrides);
        cmd!.Execute();

        overrides.ShouldBeEmpty();
    }

    [Fact]
    public void Preexisting_override_entry_is_not_overwritten_and_undo_leaves_it_alone()
    {
        var template = BuildTemplate("MyGfCell", "component = gf.components.straight()", "gdsfactory");
        var canvas = new DesignCanvasViewModel();
        var overrides = new Dictionary<string, NazcaCodeOverride>();

        var cmd = PlaceComponentCommand.TryCreate(canvas, template, 0, 0, overrides);
        cmd!.Execute();
        var identifier = overrides.Keys.Single();

        cmd.Undo();
        var existing = new NazcaCodeOverride { RawCode = "keep-me", Backend = OverrideBackend.Nazca };
        overrides[identifier] = existing;

        cmd.Execute();
        overrides[identifier].ShouldBeSameAs(existing);

        cmd.Undo();
        overrides[identifier].ShouldBeSameAs(existing);
    }

    [Fact]
    public void No_override_store_does_not_throw()
    {
        var template = BuildTemplate("MyGfCell", "component = gf.components.straight()", "gdsfactory");
        var canvas = new DesignCanvasViewModel();

        var cmd = PlaceComponentCommand.TryCreate(canvas, template, 0, 0, overrideStore: null);
        cmd!.Execute();
        cmd.Undo();
    }
}
