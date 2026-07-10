using CAP.Avalonia.Services;
using CAP_Core.Components.Core;
using CAP_DataAccess.Components.ComponentDraftMapper;
using Shouldly;
using Xunit;

namespace UnitTests.Components.PolarizationDomain;

/// <summary>
/// Tests PDK-level polarization persistence: declaring per-pin polarization
/// in JSON, backward-compatible TE default for old PDKs, load-time rejection
/// of invalid values, and save/load round-trips (issue #534).
/// </summary>
public class PolarizationPdkPersistenceTests
{
    private static string BuildPdkJson(string pinJson, string componentName = "Test Component", string nazcaFunction = "test.comp") => $$"""
        {
            "name": "Test PDK",
            "components": [
                {
                    "name": "{{componentName}}",
                    "category": "Test",
                    "nazcaFunction": "{{nazcaFunction}}",
                    "widthMicrometers": 100,
                    "heightMicrometers": 50,
                    "nazcaOriginOffsetX": 0, "nazcaOriginOffsetY": 25,
                    "pins": [ {{pinJson}} ]
                }
            ]
        }
        """;

    private const string TmPinJson =
        """{ "name": "a0", "offsetXMicrometers": 0, "offsetYMicrometers": 25, "angleDegrees": 180, "polarization": "TM" }""";

    private const string PlainPinJson =
        """{ "name": "a0", "offsetXMicrometers": 0, "offsetYMicrometers": 25, "angleDegrees": 180 }""";

    [Fact]
    public void LoadFromJson_PinWithPolarizationField_ParsesIt()
    {
        var pdk = new PdkLoader().LoadFromJson(BuildPdkJson(TmPinJson));

        pdk.Components[0].Pins[0].Polarization.ShouldBe("TM");
    }

    [Fact]
    public void LoadFromJson_PinWithoutPolarization_TemplateDefaultsToTe()
    {
        var pdk = new PdkLoader().LoadFromJson(BuildPdkJson(PlainPinJson));

        var template = PdkTemplateConverter.ConvertToTemplate(pdk.Components[0], pdk.Name, null);

        template.PinDefinitions[0].Polarization.ShouldBe(PolarizationKind.TE);
    }

    [Fact]
    public void LoadFromJson_TmNamedComponentWithoutPolarizationField_InfersTm()
    {
        var pdk = new PdkLoader().LoadFromJson(BuildPdkJson(
            PlainPinJson, componentName: "GC TM 1550 8deg", nazcaFunction: "GC_TM_1550_8degOxide_BB"));

        var template = PdkTemplateConverter.ConvertToTemplate(pdk.Components[0], pdk.Name, null);

        template.PinDefinitions[0].Polarization.ShouldBe(PolarizationKind.TM);
    }

    [Fact]
    public void LoadFromJson_InvalidPolarizationValue_ThrowsValidationError()
    {
        var badPin = """{ "name": "a0", "offsetXMicrometers": 0, "offsetYMicrometers": 25, "angleDegrees": 180, "polarization": "circular" }""";

        var exception = Should.Throw<PdkValidationException>(
            () => new PdkLoader().LoadFromJson(BuildPdkJson(badPin)));

        exception.Errors.ShouldContain(e => e.Contains("polarization") && e.Contains("circular"));
    }

    [Fact]
    public void SaveAndReload_PreservesPolarization()
    {
        var pdk = new PdkLoader().LoadFromJson(BuildPdkJson(TmPinJson));
        var filePath = Path.Combine(Path.GetTempPath(), $"polarization_pdk_{Guid.NewGuid():N}.json");

        try
        {
            new PdkJsonSaver().SaveToFile(pdk, filePath);
            var reloaded = new PdkLoader().LoadFromFile(filePath);

            reloaded.Components[0].Pins[0].Polarization.ShouldBe("TM");
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void CreateFromTemplate_PropagatesPolarizationToLogicalAndPhysicalPins()
    {
        var pdk = new PdkLoader().LoadFromJson(BuildPdkJson(TmPinJson));
        var template = PdkTemplateConverter.ConvertToTemplate(pdk.Components[0], pdk.Name, null);

        var component = CAP.Avalonia.ViewModels.Library.ComponentTemplates
            .CreateFromTemplate(template, 0, 0);

        var physicalPin = component.PhysicalPins.Single();
        physicalPin.Polarization.ShouldBe(PolarizationKind.TM);
        physicalPin.LogicalPin.Polarization.ShouldBe(PolarizationKind.TM);
    }
}
