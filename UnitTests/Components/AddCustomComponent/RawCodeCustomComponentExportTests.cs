using System.Collections.Generic;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP_DataAccess.Persistence.PIR;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

/// <summary>
/// Export verification for raw-code authored custom components (#701): the override
/// seeded on placement by <see cref="RawCodeOverrideSeeder"/> must drive the
/// <see cref="SimpleNazcaExporter"/>'s raw-code path — the pasted cell code ends up in
/// the export instead of a PDK stub.
/// </summary>
public class RawCodeCustomComponentExportTests
{
    private const string RawCode = """
        import nazca as nd

        def component():
            with nd.Cell() as C:
                nd.strt(length=42).put()
                return C
        """;

    [Fact]
    public void Seeded_override_exports_the_pasted_cell_code_not_a_stub()
    {
        // Arrange: a component placed from a raw-code template, with its override seeded
        // exactly as MainViewModel wires it on placement.
        var component = TestComponentFactory.CreateBasicComponent();
        component.Identifier = "Raw Custom";
        var template = new ComponentTemplate
        {
            Name = "Raw Custom",
            WidthMicrometers = 42,
            HeightMicrometers = 1,
            NazcaOriginOffsetX = 0,
            NazcaOriginOffsetY = 1,
            RawCode = RawCode,
            RawCodeBackend = "nazca",
            PinDefinitions = new[]
            {
                new PinDefinition("a0", 0, 0.5, 180),
                new PinDefinition("b0", 42, 0.5, 0),
            },
        };
        var overrides = new Dictionary<string, NazcaCodeOverride>();
        RawCodeOverrideSeeder.Seed(component, template, overrides).ShouldBeTrue();

        var canvas = new DesignCanvasViewModel();
        canvas.Components.Add(new ComponentViewModel(component));

        // Act
        var result = new SimpleNazcaExporter().Export(canvas, overrides: overrides);

        // Assert: the pasted cell code is exported via the raw-code factory path,
        // anchored on the bbox corner the seeder derived from the template offsets.
        result.ShouldContain("nd.strt(length=42).put()");
        result.ShouldContain("(raw-code override, bbox-anchored)");
    }
}
