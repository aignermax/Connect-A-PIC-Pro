using CAP_DataAccess.Import.Gds;
using CAP_DataAccess.Import.Gds.LayerCensus;
using Shouldly;
using Xunit;

namespace UnitTests.Import.Gds.LayerCensus;

/// <summary>
/// Tests for <see cref="GdsLayerCensus"/>: per-(layer, datatype) element counts
/// over all cells of a library, including the single-line text distinction and
/// the text-bearing cell list.
/// </summary>
public class GdsLayerCensusTests
{
    private static GdsLibrary Library(params GdsCell[] cells)
    {
        var library = new GdsLibrary();
        foreach (var cell in cells)
            library.Cells[cell.Name] = cell;
        return library;
    }

    private static GdsCell Cell(string name, params GdsElement[] elements)
    {
        var cell = new GdsCell { Name = name };
        cell.Elements.AddRange(elements);
        return cell;
    }

    private static GdsPolygon Polygon(int layer, int datatype) => new()
    {
        Layer = layer,
        DataType = datatype,
        Points = new[] { new GdsPoint(0, 0), new GdsPoint(1, 0), new GdsPoint(1, 1), new GdsPoint(0, 0) },
    };

    private static GdsPath Path(int layer, int datatype) => new()
    {
        Layer = layer,
        DataType = datatype,
        WidthMicrometers = 0.5,
        Points = new[] { new GdsPoint(0, 0), new GdsPoint(10, 0) },
    };

    private static GdsText Text(int layer, int texttype, string text) => new()
    {
        Layer = layer,
        TextType = texttype,
        Text = text,
    };

    [Fact]
    public void Build_CountsElementsPerPair_AcrossAllCells()
    {
        var library = Library(
            Cell("a", Polygon(1, 0), Polygon(1, 0), Path(1, 0), Text(1, 10, "in")),
            Cell("b", Polygon(1, 0), Text(1, 10, "out")));

        var census = GdsLayerCensus.Build(library);

        census.Count.ShouldBe(2);
        var waveguide = census.Single(e => e is { Layer: 1, Datatype: 0 });
        waveguide.PolygonCount.ShouldBe(3);
        waveguide.PathCount.ShouldBe(1);
        waveguide.TextCount.ShouldBe(0);
        var ports = census.Single(e => e is { Layer: 1, Datatype: 10 });
        ports.TextCount.ShouldBe(2);
        ports.PolygonCount.ShouldBe(0);
    }

    [Fact]
    public void Build_TextTexttypeIsTheDatatypeKey()
    {
        var library = Library(Cell("a", Text(56, 3, "p1"), Polygon(56, 0)));

        var census = GdsLayerCensus.Build(library);

        census.Single(e => e is { Layer: 56, Datatype: 3 }).TextCount.ShouldBe(1);
        census.Single(e => e is { Layer: 56, Datatype: 0 }).PolygonCount.ShouldBe(1);
    }

    [Fact]
    public void Build_DistinguishesSingleLineFromMultiLineTexts()
    {
        var library = Library(Cell("a", Text(1, 10, "opt_in"), Text(1, 10, "cellname: x\npdk: y")));

        var entry = GdsLayerCensus.Build(library).Single();

        entry.TextCount.ShouldBe(2);
        entry.SingleLineTextCount.ShouldBe(1);
    }

    [Fact]
    public void Build_ListsTextBearingCellsSortedAndDistinct()
    {
        var library = Library(
            Cell("zeta", Text(1, 10, "a"), Text(1, 10, "b")),
            Cell("alpha", Text(1, 10, "c")));

        var entry = GdsLayerCensus.Build(library).Single();

        entry.TextCellNames.ShouldBe(new[] { "alpha", "zeta" });
    }

    [Fact]
    public void Build_OrdersEntriesByLayerThenDatatype()
    {
        var library = Library(Cell("a", Polygon(2, 1), Polygon(1, 5), Polygon(2, 0), Polygon(1, 0)));

        var census = GdsLayerCensus.Build(library);

        census.Select(e => (e.Layer, e.Datatype))
            .ShouldBe(new[] { (1, 0), (1, 5), (2, 0), (2, 1) });
    }

    [Fact]
    public void Build_EmptyLibrary_YieldsNoEntries()
    {
        GdsLayerCensus.Build(Library()).ShouldBeEmpty();
    }
}
