using System.Collections.Generic;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.ViewModels.Library;
using CAP_DataAccess.Persistence.PIR;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

/// <summary>
/// Covers <see cref="RawCodeOverrideSeeder"/> (#701): placing a raw-code authored custom
/// component seeds its per-instance <see cref="NazcaCodeOverride"/> so the existing
/// #559/#637 raw-code pipeline previews and exports the real geometry.
/// </summary>
public class RawCodeOverrideSeederTests
{
    private static ComponentTemplate RawTemplate(string backend = "nazca") => new()
    {
        Name = "RawComp",
        WidthMicrometers = 10,
        HeightMicrometers = 4,
        NazcaOriginOffsetX = 3,
        NazcaOriginOffsetY = 4,
        RawCode = "with nd.Cell('c') as c: pass",
        RawCodeBackend = backend,
        PinDefinitions = new[]
        {
            new PinDefinition("o1", 0, 2, 180),
            new PinDefinition("o2", 10, 2, 0),
        },
    };

    [Fact]
    public void Seed_creates_an_override_with_code_geometry_anchor_and_pins()
    {
        var component = TestComponentFactory.CreateBasicComponent();
        var overrides = new Dictionary<string, NazcaCodeOverride>();

        RawCodeOverrideSeeder.Seed(component, RawTemplate(), overrides).ShouldBeTrue();

        var entry = overrides[component.Identifier];
        entry.RawCode.ShouldBe("with nd.Cell('c') as c: pass");
        entry.Backend.ShouldBe(OverrideBackend.Nazca);
        entry.OverrideWidthMicrometers.ShouldBe(10);
        entry.OverrideHeightMicrometers.ShouldBe(4);
        // Export anchor: bboxXMin = -offsetX, bboxYMax = offsetY (cell-internal coordinates).
        entry.OverrideBboxXMinMicrometers.ShouldBe(-3);
        entry.OverrideBboxYMaxMicrometers.ShouldBe(4);
        entry.OverridePins!.Count.ShouldBe(2);
        entry.OverridePins[0].Name.ShouldBe("o1");
        entry.OverridePins[0].AngleDegrees.ShouldBe(180);
        entry.TemplatePins!.Count.ShouldBe(2);
        // Pins match the template by construction, so the saved simulation model stays valid.
        entry.HasNoSimulationModel.ShouldBeFalse();
    }

    [Fact]
    public void Seed_maps_the_gdsfactory_backend()
    {
        var component = TestComponentFactory.CreateBasicComponent();
        var overrides = new Dictionary<string, NazcaCodeOverride>();

        RawCodeOverrideSeeder.Seed(component, RawTemplate(backend: "gdsfactory"), overrides);

        overrides[component.Identifier].Backend.ShouldBe(OverrideBackend.GdsFactory);
    }

    [Fact]
    public void Seed_is_a_noop_for_templates_without_raw_code()
    {
        var component = TestComponentFactory.CreateBasicComponent();
        var overrides = new Dictionary<string, NazcaCodeOverride>();
        var template = RawTemplate();
        template.RawCode = null;

        RawCodeOverrideSeeder.Seed(component, template, overrides).ShouldBeFalse();
        overrides.ShouldBeEmpty();
    }

    [Fact]
    public void Seed_never_clobbers_an_existing_override()
    {
        var component = TestComponentFactory.CreateBasicComponent();
        var existing = new NazcaCodeOverride { RawCode = "user edited code" };
        var overrides = new Dictionary<string, NazcaCodeOverride> { [component.Identifier] = existing };

        RawCodeOverrideSeeder.Seed(component, RawTemplate(), overrides).ShouldBeFalse();

        overrides[component.Identifier].ShouldBeSameAs(existing);
    }
}
