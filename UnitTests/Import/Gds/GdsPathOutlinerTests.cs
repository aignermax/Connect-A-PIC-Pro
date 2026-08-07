using CAP_DataAccess.Import.Gds;
using Shouldly;

namespace UnitTests.Import.Gds;

/// <summary>
/// Unit tests for <see cref="GdsPathOutliner"/>: per-segment quad geometry
/// (horizontal/vertical/diagonal), the PATHTYPE 1/2 half-width end extension,
/// multi-segment expansion, degenerate centerlines and the zero-width skip.
/// All coordinates are micrometers in the path's own (untransformed) space.
/// </summary>
public class GdsPathOutlinerTests
{
    private const double Tolerance = 1e-9;

    private static GdsPath Path(double width, int pathType, params (double X, double Y)[] points) =>
        new()
        {
            Layer = 3,
            DataType = 1,
            WidthMicrometers = width,
            PathType = pathType,
            Points = points.Select(p => new GdsPoint(p.X, p.Y)).ToList(),
        };

    private static void ShouldMatchRing(GdsPolygon quad, params (double X, double Y)[] expected)
    {
        quad.Points.Count.ShouldBe(expected.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            quad.Points[i].X.ShouldBe(expected[i].X, Tolerance, $"point {i} X");
            quad.Points[i].Y.ShouldBe(expected[i].Y, Tolerance, $"point {i} Y");
        }
    }

    [Fact]
    public void Outline_HorizontalSegment_FlushCaps_YieldsExactQuad()
    {
        var quads = GdsPathOutliner.Outline(Path(1.0, pathType: 0, (0, 0), (4, 0)));

        var quad = quads.ShouldHaveSingleItem();
        quad.Layer.ShouldBe(3);
        quad.DataType.ShouldBe(1);
        ShouldMatchRing(quad, (0, 0.5), (4, 0.5), (4, -0.5), (0, -0.5), (0, 0.5));
    }

    [Fact]
    public void Outline_VerticalSegment_YieldsExactQuad()
    {
        var quads = GdsPathOutliner.Outline(Path(1.0, pathType: 0, (0, 0), (0, 4)));

        ShouldMatchRing(quads.ShouldHaveSingleItem(),
            (-0.5, 0), (-0.5, 4), (0.5, 4), (0.5, 0), (-0.5, 0));
    }

    [Fact]
    public void Outline_DiagonalSegment_QuadIsPerpendicularToTheCenterline()
    {
        // 3-4-5 triangle: direction (0.6, 0.8), normal (-0.8, 0.6), half width 1.
        var quads = GdsPathOutliner.Outline(Path(2.0, pathType: 0, (0, 0), (3, 4)));

        ShouldMatchRing(quads.ShouldHaveSingleItem(),
            (-0.8, 0.6), (2.2, 4.6), (3.8, 3.4), (0.8, -0.6), (-0.8, 0.6));
    }

    [Fact]
    public void Outline_PathType2_ExtendsBothEndsByHalfWidth()
    {
        var quads = GdsPathOutliner.Outline(Path(1.0, pathType: 2, (0, 0), (4, 0)));

        ShouldMatchRing(quads.ShouldHaveSingleItem(),
            (-0.5, 0.5), (4.5, 0.5), (4.5, -0.5), (-0.5, -0.5), (-0.5, 0.5));
    }

    [Fact]
    public void Outline_PathType1_RoundCaps_ApproximatedAsHalfWidthExtension()
    {
        // Documented v1 approximation: round caps behave like PATHTYPE 2.
        var round = GdsPathOutliner.Outline(Path(1.0, pathType: 1, (0, 0), (4, 0)));
        var extended = GdsPathOutliner.Outline(Path(1.0, pathType: 2, (0, 0), (4, 0)));

        round.ShouldHaveSingleItem().Points.ShouldBe(extended.ShouldHaveSingleItem().Points);
    }

    [Fact]
    public void Outline_LPath_YieldsOneQuadPerSegment()
    {
        var quads = GdsPathOutliner.Outline(Path(1.0, pathType: 0, (0, 0), (4, 0), (4, 4)));

        quads.Count.ShouldBe(2);
        ShouldMatchRing(quads[0], (0, 0.5), (4, 0.5), (4, -0.5), (0, -0.5), (0, 0.5));
        ShouldMatchRing(quads[1], (3.5, 0), (3.5, 4), (4.5, 4), (4.5, 0), (3.5, 0));
    }

    [Fact]
    public void Outline_LPath_PathType2_ExtendsOnlyTheOuterEnds()
    {
        var quads = GdsPathOutliner.Outline(Path(1.0, pathType: 2, (0, 0), (4, 0), (4, 4)));

        quads.Count.ShouldBe(2);
        // First segment: start extended to x = -0.5, interior end stays at x = 4.
        ShouldMatchRing(quads[0], (-0.5, 0.5), (4, 0.5), (4, -0.5), (-0.5, -0.5), (-0.5, 0.5));
        // Second segment: interior start stays at y = 0, end extended to y = 4.5.
        ShouldMatchRing(quads[1], (3.5, 0), (3.5, 4.5), (4.5, 4.5), (4.5, 0), (3.5, 0));
    }

    [Fact]
    public void Outline_ZeroWidthPath_YieldsNothing()
    {
        // Width 0 means the WIDTH record was absent — no enclosed area exists,
        // and a fabricated hairline could invent route connections.
        GdsPathOutliner.Outline(Path(0.0, pathType: 0, (0, 0), (4, 0))).ShouldBeEmpty();
    }

    [Fact]
    public void Outline_ZeroLengthSegments_AreDropped_CapExtensionAppliesAtTrueEnds()
    {
        var quads = GdsPathOutliner.Outline(
            Path(1.0, pathType: 2, (0, 0), (0, 0), (4, 0), (4, 0)));

        // The duplicated points collapse into one segment whose BOTH ends are
        // the path's true ends — the PATHTYPE 2 extension applies to both.
        ShouldMatchRing(quads.ShouldHaveSingleItem(),
            (-0.5, 0.5), (4.5, 0.5), (4.5, -0.5), (-0.5, -0.5), (-0.5, 0.5));
    }

    [Fact]
    public void Outline_SinglePoint_CappedPath_YieldsSquareCap()
    {
        var quads = GdsPathOutliner.Outline(Path(1.0, pathType: 2, (2, 3)));

        ShouldMatchRing(quads.ShouldHaveSingleItem(),
            (1.5, 2.5), (2.5, 2.5), (2.5, 3.5), (1.5, 3.5), (1.5, 2.5));
    }

    [Fact]
    public void Outline_SinglePoint_FlushPath_YieldsNothing()
    {
        GdsPathOutliner.Outline(Path(1.0, pathType: 0, (2, 3))).ShouldBeEmpty();
    }

    [Fact]
    public void ExpandDrawnGeometry_KeepsPolygonsVerbatim_AndOutlinesPaths()
    {
        var polygon = new GdsPolygon
        {
            Layer = 5,
            DataType = 0,
            Points = new[] { new GdsPoint(0, 0), new GdsPoint(1, 0), new GdsPoint(0, 1), new GdsPoint(0, 0) },
        };
        var elements = new GdsElement[]
        {
            polygon,
            Path(1.0, pathType: 0, (0, 0), (4, 0), (4, 4)),
            new GdsText { Text = "label" },
        };

        var expanded = GdsPathOutliner.ExpandDrawnGeometry(elements).ToList();

        expanded.Count.ShouldBe(3, "1 polygon verbatim + 2 quads from the 2-segment path");
        expanded[0].ShouldBeSameAs(polygon);
        expanded[1].Layer.ShouldBe(3);
        expanded[2].Layer.ShouldBe(3);
    }
}
