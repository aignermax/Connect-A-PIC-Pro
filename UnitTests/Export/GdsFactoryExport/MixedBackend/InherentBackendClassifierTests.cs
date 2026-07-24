using CAP.Avalonia.Services.GdsFactoryExport.MixedBackend;
using CAP.Avalonia.ViewModels.Library;
using Shouldly;
using Xunit;

namespace UnitTests.Export.GdsFactoryExport.MixedBackend;

/// <summary>Tests for the inherent-backend classification of placed components.</summary>
public class InherentBackendClassifierTests
{
    private static readonly ComponentTemplate[] EmptyLibrary = Array.Empty<ComponentTemplate>();

    [Fact]
    public void Classify_GdsFactoryFunctionSet_IsGdsFactoryNative()
    {
        var component = TestComponentFactory.CreateBasicComponent();
        component.GdsFactoryFunction = "cspdk.sin300.mmi1x2";
        component.NazcaFunctionName = "";

        InherentBackendClassifier.Classify(component, EmptyLibrary)
            .ShouldBe(InherentBackend.GdsFactory);
    }

    [Fact]
    public void Classify_NazcaFunctionOnly_IsNazcaNative()
    {
        var component = TestComponentFactory.CreateBasicComponent();
        component.NazcaFunctionName = "ebeam_y_1550";

        InherentBackendClassifier.Classify(component, EmptyLibrary)
            .ShouldBe(InherentBackend.Nazca);
    }

    [Fact]
    public void Classify_NoFunctionsAtAll_IsNazcaNative()
    {
        // Built-ins / stubs without any PDK function fall to the nazca exporter,
        // matching the split SimpleNazcaExporter applies.
        var component = TestComponentFactory.CreateBasicComponent();
        component.NazcaFunctionName = "";

        InherentBackendClassifier.Classify(component, EmptyLibrary)
            .ShouldBe(InherentBackend.Nazca);
    }

    [Theory]
    [InlineData("gdsfactory", InherentBackend.GdsFactory)]
    [InlineData("GDSFactory", InherentBackend.GdsFactory)]   // case-insensitive
    [InlineData("nazca", InherentBackend.Nazca)]
    public void Classify_RawCodeComponent_FollowsTemplateRawCodeBackend(
        string rawCodeBackend, InherentBackend expected)
    {
        // Raw-code components carry NO PDK function of their own — the placed component
        // gets the synthesized nazca_<name> function, and the backend lives on the template.
        var template = new ComponentTemplate
        {
            Name = "My Raw Comp",
            RawCode = "def build():\n    pass",
            RawCodeBackend = rawCodeBackend,
        };
        var component = TestComponentFactory.CreateBasicComponent();
        component.NazcaFunctionName = "nazca_my_raw_comp";   // synthesized fallback name

        InherentBackendClassifier.Classify(component, new[] { template }).ShouldBe(expected);
    }

    [Fact]
    public void Classify_RawCodeTemplateWithExplicitNazcaFunction_IsMatchedByThatName()
    {
        var template = new ComponentTemplate
        {
            Name = "Custom",
            NazcaFunctionName = "my_custom_cell",
            RawCode = "code",
            RawCodeBackend = "gdsfactory",
        };
        var component = TestComponentFactory.CreateBasicComponent();
        component.NazcaFunctionName = "my_custom_cell";

        InherentBackendClassifier.Classify(component, new[] { template })
            .ShouldBe(InherentBackend.GdsFactory);
    }

    [Fact]
    public void Classify_TemplateWithoutRawCode_FallsBackToComponentFunctions()
    {
        // A resolved template without raw code must not change the PDK-function rule.
        var template = new ComponentTemplate { Name = "Y", NazcaFunctionName = "ebeam_y_1550" };
        var component = TestComponentFactory.CreateBasicComponent();
        component.NazcaFunctionName = "ebeam_y_1550";

        InherentBackendClassifier.Classify(component, new[] { template })
            .ShouldBe(InherentBackend.Nazca);
    }
}
