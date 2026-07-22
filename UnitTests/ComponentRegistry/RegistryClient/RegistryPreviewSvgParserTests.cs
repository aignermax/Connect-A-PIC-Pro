using System.Globalization;
using CAP_Core.ComponentRegistry.RegistryClient;
using Shouldly;
using Xunit;

namespace UnitTests.ComponentRegistry.RegistryClient;

/// <summary>
/// Verifies <see cref="RegistryPreviewSvgParser"/> against the real preview
/// SVGs produced by the registry's <c>render_previews.py</c> (rect background
/// plus polygon-per-GDS-polygon), and its never-throw behavior on garbage.
/// </summary>
public class RegistryPreviewSvgParserTests
{
    [Fact]
    public void TryParse_YBranchFixture_ExtractsViewBoxBackgroundAndPolygon()
    {
        var svg = RegistryTestHarness.ReadFixture("preview-y-branch-1x2.svg");

        var document = RegistryPreviewSvgParser.TryParse(svg).ShouldNotBeNull();

        document.ViewBoxMinX.ShouldBe(-11.275, 0.001);
        document.ViewBoxMinY.ShouldBe(-2.525, 0.001);
        document.ViewBoxWidth.ShouldBe(28.050, 0.001);
        document.ViewBoxHeight.ShouldBe(5.050, 0.001);
        document.BackgroundColor.ShouldBe("#12161d");

        var polygon = document.Polygons.ShouldHaveSingleItem();
        polygon.FillColor.ShouldBe("#38bdf8");
        polygon.FillOpacity.ShouldBe(0.92, 0.001);
        polygon.Points.Count.ShouldBe(16);
        polygon.Points[0].ShouldBe((0.000, 1.250));
        polygon.Points[2].ShouldBe((-10.000, 0.250));
    }

    [Fact]
    public void TryParse_RingResonatorFixture_ExtractsAllPolygons()
    {
        var svg = RegistryTestHarness.ReadFixture("preview-ring-resonator-r10.svg");

        var document = RegistryPreviewSvgParser.TryParse(svg).ShouldNotBeNull();

        // Bus waveguide + ring: two filled polygons, both on the waveguide layer color.
        document.Polygons.Count.ShouldBe(2);
        document.Polygons.ShouldAllBe(p => p.FillColor == "#38bdf8");
        document.Polygons.ShouldAllBe(p => p.Points.Count >= 4);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("this is not xml at all")]
    [InlineData("<html><body>404</body></html>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>")] // No viewBox, no shapes.
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\"></svg>")] // No polygons.
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"bad box\"><polygon points=\"0,0 1,0 1,1\"/></svg>")]
    public void TryParse_GarbageOrEmptyDocuments_ReturnsNull_WithoutThrowing(string svg)
    {
        RegistryPreviewSvgParser.TryParse(svg).ShouldBeNull();
    }

    [Fact]
    public void TryParse_SkipsPolygonsWithMalformedPoints_KeepsValidOnes()
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10">
              <polygon points="not,numbers at,all" fill="#ff0000"/>
              <polygon points="0,0 10,0 10,10 0,10" fill="#38bdf8"/>
            </svg>
            """;

        var document = RegistryPreviewSvgParser.TryParse(svg).ShouldNotBeNull();

        var polygon = document.Polygons.ShouldHaveSingleItem();
        polygon.FillColor.ShouldBe("#38bdf8");
        polygon.Points.Count.ShouldBe(4);
    }

    [Fact]
    public void TryParse_IsCultureInvariant()
    {
        // A de-DE runner must not read "1.250" as 1250 (comma decimal separator).
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var svg = RegistryTestHarness.ReadFixture("preview-y-branch-1x2.svg");

            var document = RegistryPreviewSvgParser.TryParse(svg).ShouldNotBeNull();

            document.ViewBoxWidth.ShouldBe(28.050, 0.001);
            document.Polygons[0].Points[0].Y.ShouldBe(1.250, 0.001);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void TryParse_AllCommittedPreviewFixtures_ParseSuccessfully()
    {
        foreach (var componentId in RegistryTestHarness.PreviewComponentIds)
        {
            var svg = RegistryTestHarness.ReadFixture($"preview-{componentId}.svg");
            var document = RegistryPreviewSvgParser.TryParse(svg);

            document.ShouldNotBeNull($"preview-{componentId}.svg must parse");
            document.Polygons.ShouldNotBeEmpty($"preview-{componentId}.svg must contain polygons");
            document.ViewBoxWidth.ShouldBeGreaterThan(0);
            document.ViewBoxHeight.ShouldBeGreaterThan(0);
        }
    }
}
