using CAP_DataAccess.Import.Gds;
using CAP_DataAccess.Import.Gds.LayerCensus;
using Shouldly;
using Xunit;

namespace UnitTests.Import.Gds.LayerCensus;

/// <summary>
/// Tests for <see cref="GdsLayerSuggestionEngine"/>: known conventions surface
/// as high-confidence suggestions, text-bearing layers become port-label
/// candidates, and top-cell route strokes become "routing, kind unknown" —
/// never a silently resolved optical/metal guess.
/// </summary>
public class GdsLayerSuggestionEngineTests
{
    private const string TopCell = "TOP";

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

    private static GdsPolygon Rectangle(int layer, int datatype, double width, double height) => new()
    {
        Layer = layer,
        DataType = datatype,
        Points = new[]
        {
            new GdsPoint(0, 0), new GdsPoint(width, 0),
            new GdsPoint(width, height), new GdsPoint(0, height), new GdsPoint(0, 0),
        },
    };

    private static GdsPath Path(int layer, int datatype) => new()
    {
        Layer = layer,
        DataType = datatype,
        WidthMicrometers = 0.5,
        Points = new[] { new GdsPoint(0, 0), new GdsPoint(50, 0) },
    };

    private static GdsText Text(int layer, int texttype, string text) => new()
    {
        Layer = layer,
        TextType = texttype,
        Text = text,
    };

    private static IReadOnlyList<GdsLayerSuggestion> Suggest(GdsLibrary library) =>
        GdsLayerSuggestionEngine.Build(library, TopCell, GdsLayerCensus.Build(library));

    [Fact]
    public void KnownConvention_WaveguidePolygons_SuggestedHighWithNamedSource()
    {
        var library = Library(Cell(TopCell), Cell("wg", Rectangle(1, 0, 10, 0.5)));

        var suggestion = Suggest(library)
            .Single(s => s is { Layer: 1, Datatype: 0, Role: GdsLayerRole.Waveguide });

        suggestion.Confidence.ShouldBe(GdsSuggestionConfidence.High);
        suggestion.Reason.ShouldContain("gdsfactory");
    }

    [Fact]
    public void KnownPortConvention_RequiresSingleLineTexts()
    {
        var library = Library(Cell(TopCell, Text(1, 10, "meta\nblob")));

        Suggest(library).ShouldNotContain(s => s.Role == GdsLayerRole.PortLabels);
    }

    [Fact]
    public void TextBearingUnknownLayer_BecomesMediumPortLabelCandidate_NamingCells()
    {
        var library = Library(Cell(TopCell), Cell("dev", Text(56, 0, "opt_1"), Text(56, 0, "opt_2")));

        var suggestion = Suggest(library)
            .Single(s => s is { Layer: 56, Datatype: 0, Role: GdsLayerRole.PortLabels });

        suggestion.Confidence.ShouldBe(GdsSuggestionConfidence.Medium);
        suggestion.Reason.ShouldContain("2 text label(s)");
        suggestion.Reason.ShouldContain("'dev'");
    }

    [Fact]
    public void TopCellPathOnUnknownLayer_BecomesRoutingKindUnknown()
    {
        var library = Library(Cell(TopCell, Path(37, 0)));

        var suggestion = Suggest(library)
            .Single(s => s is { Layer: 37, Datatype: 0 });

        suggestion.Role.ShouldBe(GdsLayerRole.RoutingUnknown);
        suggestion.Confidence.ShouldBe(GdsSuggestionConfidence.Low);
        suggestion.Reason.ShouldContain(TopCell);
    }

    [Fact]
    public void TopCellLongThinPolygon_CountsAsRouteStroke()
    {
        var library = Library(Cell(TopCell, Rectangle(37, 0, 100, 0.5)));

        Suggest(library).ShouldContain(s =>
            s.Layer == 37 && s.Role == GdsLayerRole.RoutingUnknown);
    }

    [Fact]
    public void TopCellSquarePolygon_IsNotARouteStroke()
    {
        var library = Library(Cell(TopCell, Rectangle(37, 0, 10, 10)));

        Suggest(library).ShouldBeEmpty();
    }

    [Fact]
    public void KnownRouteLayer_NotDuplicatedAsRoutingUnknown()
    {
        var library = Library(Cell(TopCell, Path(11, 0)));

        var suggestions = Suggest(library);

        suggestions.ShouldContain(s => s.Layer == 11 && s.Role == GdsLayerRole.Metal);
        suggestions.ShouldNotContain(s => s.Role == GdsLayerRole.RoutingUnknown);
    }

    [Fact]
    public void StrokesInChildCellsOnly_YieldNoRouteCandidate()
    {
        var library = Library(Cell(TopCell), Cell("child", Path(37, 0)));

        Suggest(library).ShouldNotContain(s => s.Role == GdsLayerRole.RoutingUnknown);
    }

    [Fact]
    public void MissingTopCell_YieldsNoRouteCandidates_WithoutThrowing()
    {
        var library = Library(Cell("other", Text(56, 0, "p")));

        var suggestions = GdsLayerSuggestionEngine.Build(
            library, "does-not-exist", GdsLayerCensus.Build(library));

        suggestions.ShouldNotContain(s => s.Role == GdsLayerRole.RoutingUnknown);
    }
}
