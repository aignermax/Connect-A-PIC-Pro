using CAP_DataAccess.Components.ComponentDraftMapper;
using Shouldly;
using Xunit;

namespace UnitTests.Components;

/// <summary>
/// PDK loading of active components (issue #529): a component may declare a
/// <c>compactModel</c>; unknown model names must fail validation loudly —
/// never silently degrade to passive behaviour.
/// </summary>
public class PdkLoaderCompactModelTests
{
    private static string PdkJson(string compactModelBlock) => $@"{{
        ""name"": ""Test PDK"",
        ""components"": [
            {{
                ""name"": ""Photodiode"",
                ""category"": ""Detectors"",
                ""nazcaFunction"": ""test.pd"",
                ""widthMicrometers"": 50,
                ""heightMicrometers"": 20,
                ""nazcaOriginOffsetX"": 0, ""nazcaOriginOffsetY"": 10,
                ""pins"": [
                    {{ ""name"": ""in"", ""offsetXMicrometers"": 0, ""offsetYMicrometers"": 10, ""angleDegrees"": 180 }}
                ]{compactModelBlock}
            }}
        ]
    }}";

    [Fact]
    public void LoadFromJson_KnownCompactModel_LoadsWithParameters()
    {
        var loader = new PdkLoader();
        var json = PdkJson(@",
            ""compactModel"": ""PhotodiodeRc"",
            ""compactModelParameters"": {
                ""responsivityAmpsPerWatt"": 0.9,
                ""rcTimeConstantSeconds"": 2e-11
            }");

        var pdk = loader.LoadFromJson(json);

        var comp = pdk.Components[0];
        comp.CompactModel.ShouldBe("PhotodiodeRc");
        comp.CompactModelParameters.ShouldNotBeNull();
        comp.CompactModelParameters["responsivityAmpsPerWatt"].ShouldBe(0.9);
        comp.CompactModelParameters["rcTimeConstantSeconds"].ShouldBe(2e-11);
    }

    [Fact]
    public void LoadFromJson_UnknownCompactModel_FailsValidationWithClearError()
    {
        var loader = new PdkLoader();
        var json = PdkJson(@", ""compactModel"": ""Unknown""");

        var ex = Should.Throw<PdkValidationException>(() => loader.LoadFromJson(json));

        ex.Errors.ShouldContain(e => e.Contains("Unknown compactModel 'Unknown'"));
        ex.Errors.ShouldContain(e => e.Contains("PhotodiodeRc"));
    }

    [Fact]
    public void LoadFromJson_NoCompactModel_LoadsAsPassiveComponent()
    {
        var loader = new PdkLoader();
        var pdk = loader.LoadFromJson(PdkJson(string.Empty));

        pdk.Components[0].CompactModel.ShouldBeNull();
        pdk.Components[0].CompactModelParameters.ShouldBeNull();
    }
}
