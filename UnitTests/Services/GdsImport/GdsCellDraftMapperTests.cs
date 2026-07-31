using CAP.Avalonia.Services.GdsImport;
using CAP_DataAccess.Import.Gds;
using Shouldly;
using Xunit;

namespace UnitTests.Services.GdsImport;

/// <summary>
/// Tests for <see cref="GdsCellDraftMapper"/>: GDS cell draft → PDK component
/// draft mapping (name sanitization, pins, black-box S-matrix, raw-code token
/// substitution, outline mapping).
/// </summary>
public class GdsCellDraftMapperTests
{
    private const double Tolerance = 1e-9;

    private static GdsCellDraft Draft(string cellName = "wg") => new()
    {
        CellName = cellName,
        WidthUm = 10,
        HeightUm = 4,
        Pins = new[]
        {
            new DetectedPin { Name = "in", XUm = 0, YUm = 2, AngleDegrees = 180, Source = DetectedPinSource.Label },
            new DetectedPin { Name = "out", XUm = 10, YUm = 2, AngleDegrees = 0, Source = DetectedPinSource.Label },
        },
        RawCode = $"def component():\n    return nd.load_gds(filename=\"{GdsHierarchyImporter.GdsFileNameToken}\", cellname=\"wg\")\n",
    };

    [Theory]
    [InlineData("wgA", "wgA")]
    [InlineData("my cell", "my_cell")]
    [InlineData("cell\"quoted\"", "cell_quoted_")]
    [InlineData("weird/slash\\back", "weird_slash_back")]
    [InlineData("cell#1(2)", "cell_1_2_")]
    [InlineData("mzi.v2-final", "mzi.v2-final")]
    [InlineData("", "gds_cell")]
    public void SanitizeComponentName_ReplacesInvalidCharactersDeterministically(string cellName, string expected) =>
        GdsCellDraftMapper.SanitizeComponentName(cellName).ShouldBe(expected);

    [Fact]
    public void Map_SetsDimensionsCategoryAndBlackBoxDefaults()
    {
        var result = GdsCellDraftMapper.Map(Draft("wgA"), "/tmp/lib/circuit.gds");

        result.Name.ShouldBe("wgA");
        result.Category.ShouldBe(GdsCellDraftMapper.ImportCategory);
        result.WidthMicrometers.ShouldBe(10, Tolerance);
        result.HeightMicrometers.ShouldBe(4, Tolerance);
        result.SMatrix.ShouldBeNull("GDS imports are black boxes — no simulation model");
        result.RawCodeBackend.ShouldBe("nazca");
        result.NazcaFunction.ShouldBeNull("raw-code components carry no nazca function (mirrors CustomComponentDraftFactory)");
    }

    [Fact]
    public void Map_MapsPinsToPhysicalPinDrafts()
    {
        var result = GdsCellDraftMapper.Map(Draft(), "/tmp/lib/circuit.gds");

        result.Pins.Count.ShouldBe(2);
        result.Pins[0].Name.ShouldBe("in");
        result.Pins[0].OffsetXMicrometers.ShouldBe(0, Tolerance);
        result.Pins[0].OffsetYMicrometers.ShouldBe(2, Tolerance);
        result.Pins[0].AngleDegrees.ShouldBe(180, Tolerance);
        result.Pins[1].Name.ShouldBe("out");
        result.Pins[1].OffsetXMicrometers.ShouldBe(10, Tolerance);
        result.Pins[1].AngleDegrees.ShouldBe(0, Tolerance);
    }

    [Fact]
    public void Map_SubstitutesGdsFileNameToken_WithPythonEscapedAbsolutePath()
    {
        var result = GdsCellDraftMapper.Map(Draft(), @"C:\Users\u\user-pdks\circuit.gds");

        result.RawCode.ShouldNotContain(GdsHierarchyImporter.GdsFileNameToken);
        result.RawCode.ShouldContain(@"filename=""C:\\Users\\u\\user-pdks\\circuit.gds""");
        result.RawCode.ShouldContain("cellname=\"wg\"");
    }

    [Fact]
    public void Map_MapsOutlinesFieldByField()
    {
        var draft = Draft();
        draft = draft with
        {
            Outlines = new[]
            {
                new GdsOutlinePolygon
                {
                    Layer = 1,
                    DataType = 0,
                    Points = new[]
                    {
                        new GdsOutlinePoint(0, 1.75), new GdsOutlinePoint(10, 1.75),
                        new GdsOutlinePoint(10, 2.25), new GdsOutlinePoint(0, 2.25),
                        new GdsOutlinePoint(0, 1.75), // closed ring: first point repeated
                    },
                },
            },
        };

        var result = GdsCellDraftMapper.Map(draft, "/tmp/lib/circuit.gds");

        var polygon = result.OutlinePolygons.ShouldHaveSingleItem();
        polygon.Layer.ShouldBe(1);
        polygon.DataType.ShouldBe(0);
        polygon.Points.Count.ShouldBe(5);
        polygon.Points[1].X.ShouldBe(10, Tolerance);
        polygon.Points[1].Y.ShouldBe(1.75, Tolerance);
        polygon.Points[3].X.ShouldBe(0, Tolerance);
        polygon.Points[3].Y.ShouldBe(2.25, Tolerance);
        polygon.Points[4].ShouldBe(polygon.Points[0], "the closed ring must survive the mapping");
    }

    [Fact]
    public void Map_NoOutlines_LeavesOutlinePolygonsNull() =>
        GdsCellDraftMapper.Map(Draft(), "/tmp/lib/circuit.gds")
            .OutlinePolygons.ShouldBeNull("null keeps the rectangle-rendering fallback");
}
