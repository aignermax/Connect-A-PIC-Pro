using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Analysis;
using CAP_Core.Components.Connections;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis;

/// <summary>
/// Acceptance tests for issue #906: pins of components instantiated from a PDK
/// carry the PDK's waveguide width/layer (per-pin value or the process' default
/// optical cross-section), so the <see cref="DesignValidator"/> pin-mismatch rule
/// fires on real designs without any test-side property stuffing. PDKs without
/// optical data keep the values null and the rule stays silent.
/// </summary>
public class DesignValidatorPdkPinMismatchTests
{
    private readonly DesignValidator _validator = new();

    [Fact]
    public void SiepicComponent_PinsCarryProcessWidthAndLayer()
    {
        var template = TestPdkLoader.LoadFromPdk("siepic-ebeam-pdk.json")
            .First(t => t.Name == "Y-Branch 1550");

        var component = ComponentTemplates.CreateFromTemplate(template, 0, 0);

        component.PhysicalPins.ShouldNotBeEmpty();
        foreach (var pin in component.PhysicalPins)
        {
            pin.WaveguideWidthMicrometers.ShouldBe(0.5, "the strip xsection of the SiEPIC process is 0.5 µm wide");
            pin.Layer.ShouldBe(1, "the strip xsection is drawn on the WG layer (GDS layer 1)");
        }
    }

    [Fact]
    public void MixedPdkDesign_WithDifferentWidths_ProducesPinMismatch()
    {
        var siepic = ComponentTemplates.CreateFromTemplate(
            TestPdkLoader.LoadFromPdk("siepic-ebeam-pdk.json").First(t => t.Name == "Y-Branch 1550"), 0, 0);
        var cornerstone = ComponentTemplates.CreateFromTemplate(
            TestPdkLoader.LoadFromPdk("cornerstone-sin-pdk.json").First(t => t.Name == "Coupler"), 500, 0);
        var connection = new WaveguideConnection
        {
            StartPin = siepic.PhysicalPins[0],
            EndPin = cornerstone.PhysicalPins[0]
        };

        var result = _validator.Validate(new[] { connection });

        // SiEPIC strip (0.5 µm on layer 1) vs Cornerstone xs_nc (1.2 µm on layer 203):
        // both the width and the layer rule fire.
        result.Count(i => i.Type == DesignIssueType.PinMismatch).ShouldBe(2);
        result.ShouldContain(i => i.Description.Contains("0.5") && i.Description.Contains("1.2"));
        result.ShouldContain(i => i.Description.Contains("layer 1") && i.Description.Contains("layer 203"));
    }

    [Fact]
    public void SamePdkDesign_MatchingPins_ProducesNoPinMismatch()
    {
        var first = ComponentTemplates.CreateFromTemplate(
            TestPdkLoader.LoadFromPdk("siepic-ebeam-pdk.json").First(t => t.Name == "Y-Branch 1550"), 0, 0);
        var second = ComponentTemplates.CreateFromTemplate(
            TestPdkLoader.LoadFromPdk("siepic-ebeam-pdk.json").First(t => t.Name == "Y-Branch 1550"), 500, 0);
        var connection = new WaveguideConnection
        {
            StartPin = first.PhysicalPins[0],
            EndPin = second.PhysicalPins[0]
        };

        var result = _validator.Validate(new[] { connection });

        result.ShouldNotContain(i => i.Type == DesignIssueType.PinMismatch);
    }

    [Fact]
    public void DemoPdk_WithoutOpticalXsection_KeepsPinsNullAndRuleSilent()
    {
        var templates = TestPdkLoader.LoadFromPdk("demo-pdk.json");
        templates.ShouldNotBeEmpty();
        var first = ComponentTemplates.CreateFromTemplate(templates[0], 0, 0);
        var second = ComponentTemplates.CreateFromTemplate(templates[0], 500, 0);

        first.PhysicalPins.ShouldAllBe(p => p.WaveguideWidthMicrometers == null && p.Layer == null,
            "the demo PDK declares no optical cross-section — legacy behavior keeps the values null");

        var connection = new WaveguideConnection
        {
            StartPin = first.PhysicalPins[0],
            EndPin = second.PhysicalPins[0]
        };
        _validator.Validate(new[] { connection })
            .ShouldNotContain(i => i.Type == DesignIssueType.PinMismatch);
    }
}
