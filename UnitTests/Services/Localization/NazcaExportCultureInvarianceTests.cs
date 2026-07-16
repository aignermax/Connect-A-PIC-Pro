using System.Globalization;
using CAP.Avalonia.Services;
using CAP_Core.Routing;
using Shouldly;
using Xunit;

namespace UnitTests.Services.Localization;

/// <summary>
/// Issue #744 hard constraint: machine-facing output must never depend on the UI
/// language. Runs Nazca segment formatting under German and Chinese cultures (where
/// the decimal separator or digit conventions differ) and asserts byte-identical
/// output to the invariant-culture baseline.
/// </summary>
public class NazcaExportCultureInvarianceTests
{
    [Theory]
    [InlineData("de-DE")]
    [InlineData("zh-CN")]
    [InlineData("es-ES")]
    public void FormatSegment_UnderForeignUiCulture_MatchesInvariantOutput(string cultureName)
    {
        var segment = new StraightSegment(0, 0, 1234.5, 0, 0);
        var baseline = SimpleNazcaExporter.FormatSegment(segment, isFirst: true);

        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(cultureName);
            CultureInfo.CurrentUICulture = new CultureInfo(cultureName);

            var localized = SimpleNazcaExporter.FormatSegment(segment, isFirst: true);

            localized.ShouldBe(baseline);
            localized.ShouldContain("length=1234.50");
            localized.ShouldNotContain("1234,50");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("zh-CN")]
    public void FormatSegment_Bend_UnderForeignUiCulture_MatchesInvariantOutput(string cultureName)
    {
        var segment = new BendSegment(50.5, 0, 50.5, 0, 90);
        var baseline = SimpleNazcaExporter.FormatSegment(segment, isFirst: true);

        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(cultureName);
            CultureInfo.CurrentUICulture = new CultureInfo(cultureName);

            SimpleNazcaExporter.FormatSegment(segment, isFirst: true).ShouldBe(baseline);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}
