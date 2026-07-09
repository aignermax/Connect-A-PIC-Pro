using CAP_DataAccess.Components.ComponentDraftMapper;
using Shouldly;
using Xunit;

namespace UnitTests.Components.PinKinds
{
    /// <summary>
    /// Tests the JSON <c>pinKind</c> field on PDK pins (Issue #519):
    /// legacy PDKs without the field default to optical, "Electrical" is parsed,
    /// and invalid values are rejected at load time.
    /// </summary>
    public class PdkLoaderPinKindTests
    {
        private static string BuildPdkJson(string pinJson) => $@"{{
            ""name"": ""Test PDK"",
            ""components"": [
                {{
                    ""name"": ""Bond Pad"",
                    ""category"": ""Electrical"",
                    ""nazcaFunction"": ""ebeam_BondPad"",
                    ""widthMicrometers"": 100,
                    ""heightMicrometers"": 100,
                    ""nazcaOriginOffsetX"": 0, ""nazcaOriginOffsetY"": 50,
                    ""pins"": [ {pinJson} ]
                }}
            ]
        }}";

        [Fact]
        public void LoadFromJson_PinWithoutPinKind_DefaultsToNull()
        {
            var loader = new PdkLoader();
            var json = BuildPdkJson(
                @"{ ""name"": ""a0"", ""offsetXMicrometers"": 0, ""offsetYMicrometers"": 50, ""angleDegrees"": 180 }");

            var pdk = loader.LoadFromJson(json);

            pdk.Components[0].Pins[0].PinKind.ShouldBeNull();
        }

        [Fact]
        public void LoadFromJson_ElectricalPinKind_IsParsed()
        {
            var loader = new PdkLoader();
            var json = BuildPdkJson(
                @"{ ""name"": ""m_pin_top"", ""offsetXMicrometers"": 50, ""offsetYMicrometers"": 0, ""angleDegrees"": 90, ""pinKind"": ""Electrical"" }");

            var pdk = loader.LoadFromJson(json);

            pdk.Components[0].Pins[0].PinKind.ShouldBe("Electrical");
        }

        [Fact]
        public void LoadFromJson_OpticalPinKind_IsAccepted()
        {
            var loader = new PdkLoader();
            var json = BuildPdkJson(
                @"{ ""name"": ""a0"", ""offsetXMicrometers"": 0, ""offsetYMicrometers"": 50, ""angleDegrees"": 180, ""pinKind"": ""Optical"" }");

            var pdk = loader.LoadFromJson(json);

            pdk.Components[0].Pins[0].PinKind.ShouldBe("Optical");
        }

        [Fact]
        public void LoadFromJson_InvalidPinKind_ThrowsValidationError()
        {
            var loader = new PdkLoader();
            var json = BuildPdkJson(
                @"{ ""name"": ""a0"", ""offsetXMicrometers"": 0, ""offsetYMicrometers"": 50, ""angleDegrees"": 180, ""pinKind"": ""Metal"" }");

            var ex = Should.Throw<PdkValidationException>(() => loader.LoadFromJson(json));
            ex.Errors.ShouldContain(e => e.Contains("pinKind"));
        }
    }
}
