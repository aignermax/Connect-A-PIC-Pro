using CAP.Avalonia.Services;
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
    public void Matches_IsTrue_WhenTupleEqualsTemplate()
    {
        var component = CreateComponent();

        TemplateGeometryMatch.Matches(
                component,
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
                component,
                templateModuleName: string.Empty,
                templateFunctionName: component.NazcaFunctionName,
                templateFunctionParameters: null)
            .ShouldBeTrue();
    }

    [Fact]
    public void Matches_IsFalse_WhenLiveTupleDiffersFromTemplate()
    {
        var component = CreateComponent();
        component.NazcaFunctionParameters = "length=42";

        TemplateGeometryMatch.Matches(
                component,
                templateModuleName: component.NazcaModuleName,
                templateFunctionName: component.NazcaFunctionName,
                templateFunctionParameters: "")
            .ShouldBeFalse();
    }
}
