using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components.Core;
using CAP_DataAccess.Components.ComponentDraftMapper;
using Shouldly;
using Xunit;

namespace UnitTests.Components.PinKinds
{
    /// <summary>
    /// Verifies that the <c>pinKind</c> JSON field propagates through the whole
    /// template pipeline (Issue #519): PDK JSON → PdkTemplateConverter →
    /// ComponentTemplates.CreateFromTemplate → logical + physical pins.
    /// </summary>
    public class PinKindTemplatePropagationTests
    {
        private const string BondPadPdkJson = @"{
            ""name"": ""Test PDK"",
            ""components"": [
                {
                    ""name"": ""Bond Pad"",
                    ""category"": ""Electrical"",
                    ""nazcaFunction"": ""ebeam_BondPad"",
                    ""widthMicrometers"": 100,
                    ""heightMicrometers"": 100,
                    ""nazcaOriginOffsetX"": 0, ""nazcaOriginOffsetY"": 50,
                    ""pins"": [
                        { ""name"": ""m_pin_top"", ""offsetXMicrometers"": 50, ""offsetYMicrometers"": 0, ""angleDegrees"": 90, ""pinKind"": ""Electrical"" },
                        { ""name"": ""opt_in"", ""offsetXMicrometers"": 0, ""offsetYMicrometers"": 50, ""angleDegrees"": 180 }
                    ]
                }
            ]
        }";

        [Fact]
        public void ConvertToTemplate_PropagatesPinKindToPinDefinitions()
        {
            var template = LoadBondPadTemplate();

            template.PinDefinitions[0].Kind.ShouldBe(MatterType.Electricity);
            template.PinDefinitions[1].Kind.ShouldBe(MatterType.Light);
        }

        [Fact]
        public void CreateFromTemplate_ElectricalPinDefinition_CreatesElectricalPins()
        {
            var template = LoadBondPadTemplate();

            var component = ComponentTemplates.CreateFromTemplate(template, 0, 0);

            var electricalPin = component.PhysicalPins.Single(p => p.Name == "m_pin_top");
            electricalPin.MatterType.ShouldBe(MatterType.Electricity);
            electricalPin.LogicalPin.MatterType.ShouldBe(MatterType.Electricity);

            var opticalPin = component.PhysicalPins.Single(p => p.Name == "opt_in");
            opticalPin.MatterType.ShouldBe(MatterType.Light);
        }

        [Fact]
        public void CreateFromTemplate_ClonedComponent_PreservesElectricalPinKind()
        {
            var template = LoadBondPadTemplate();
            var component = ComponentTemplates.CreateFromTemplate(template, 0, 0);

            var clone = (Component)component.Clone();

            var clonedPin = clone.PhysicalPins.Single(p => p.Name == "m_pin_top");
            clonedPin.MatterType.ShouldBe(MatterType.Electricity);
        }

        private static ComponentTemplate LoadBondPadTemplate()
        {
            var pdk = new PdkLoader().LoadFromJson(BondPadPdkJson);
            return PdkTemplateConverter.ConvertToTemplate(pdk.Components[0], pdk.Name, null);
        }
    }
}
