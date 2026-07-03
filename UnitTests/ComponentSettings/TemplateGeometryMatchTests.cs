using CAP.Avalonia.Services;
using CAP_DataAccess.Persistence.PIR;
using Shouldly;
using Xunit;

namespace UnitTests.ComponentSettings;

/// <summary>
/// Verifies the geometry guard for promoting an FDTD-recomputed S-matrix to the
/// PDK-template-scoped override (issue #580 E): only unmodified instances match.
/// </summary>
public class TemplateGeometryMatchTests
{
    private static CAP_Core.Components.Core.Component CreateComponent() =>
        TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();

    [Fact]
    public void Matches_IsTrue_WhenNoOverrideAndTupleEqualsTemplate()
    {
        var component = CreateComponent();

        TemplateGeometryMatch.Matches(
                component,
                activeOverride: null,
                templateModuleName: component.NazcaModuleName,
                templateFunctionName: component.NazcaFunctionName,
                templateFunctionParameters: component.NazcaFunctionParameters)
            .ShouldBeTrue();
    }

    [Fact]
    public void Matches_TreatsNullAndEmptyAsEqual()
    {
        var component = CreateComponent(); // module name is null, parameters ""

        TemplateGeometryMatch.Matches(
                component, null,
                templateModuleName: string.Empty,
                templateFunctionName: component.NazcaFunctionName,
                templateFunctionParameters: null)
            .ShouldBeTrue();
    }

    [Fact]
    public void Matches_IsFalse_WhenRawCodeOverrideIsActive()
    {
        var component = CreateComponent();
        var nazcaOverride = new NazcaCodeOverride { RawCode = "cell = nd.Cell('x')" };

        TemplateGeometryMatch.Matches(
                component, nazcaOverride,
                component.NazcaModuleName, component.NazcaFunctionName, component.NazcaFunctionParameters)
            .ShouldBeFalse();
    }

    [Fact]
    public void Matches_IsFalse_WhenParameterOverrideIsActive()
    {
        var component = CreateComponent();
        var nazcaOverride = new NazcaCodeOverride { FunctionParameters = "length=99" };

        TemplateGeometryMatch.Matches(
                component, nazcaOverride,
                component.NazcaModuleName, component.NazcaFunctionName, component.NazcaFunctionParameters)
            .ShouldBeFalse();
    }

    [Fact]
    public void Matches_IsFalse_WhenLiveTupleDiffersFromTemplate()
    {
        var component = CreateComponent();
        component.NazcaFunctionParameters = "length=42";

        TemplateGeometryMatch.Matches(
                component, null,
                templateModuleName: component.NazcaModuleName,
                templateFunctionName: component.NazcaFunctionName,
                templateFunctionParameters: "")
            .ShouldBeFalse();
    }

    [Fact]
    public void Matches_IsTrue_WhenOverrideRecordCarriesNoGeometryFields()
    {
        var component = CreateComponent();
        // A record that only snapshots template values (e.g. after "Reset to
        // template") does not modify geometry.
        var nazcaOverride = new NazcaCodeOverride
        {
            TemplateFunctionName = component.NazcaFunctionName,
            TemplateFunctionParameters = component.NazcaFunctionParameters,
        };

        TemplateGeometryMatch.Matches(
                component, nazcaOverride,
                component.NazcaModuleName, component.NazcaFunctionName, component.NazcaFunctionParameters)
            .ShouldBeTrue();
    }
}
