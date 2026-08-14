using CAP_DataAccess.Import.Gds;
using CAP_DataAccess.Import.Gds.LayerCensus;
using Shouldly;
using Xunit;

namespace UnitTests.Import.Gds.LayerCensus;

/// <summary>
/// Tests for <see cref="GdsLayerSuggestionEngine"/>: text-backed port labels
/// surface as high-confidence (auto-appliable) suggestions, metal/waveguide
/// claims from the union of known tables stay medium (layer numbers collide
/// across foundries — never silently resolved), and top-cell route strokes
/// become "routing, kind unknown".
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
    public void KnownConvention_WaveguidePolygons_SuggestedMediumWithNamedSource()
    {
        var library = Library(Cell(TopCell), Cell("wg", Rectangle(1, 0, 10, 0.5)));

        var suggestion = Suggest(library)
            .Single(s => s is { Layer: 1, Datatype: 0, Role: GdsLayerRole.Waveguide });

        // union-table claims are convention guesses — never high confidence:
        // another foundry may use the same number for metal or a marker layer
        suggestion.Confidence.ShouldBe(GdsSuggestionConfidence.Medium);
        suggestion.Reason.ShouldContain("gdsfactory");
    }

    [Fact]
    public void KnownConvention_MetalClaim_IsMediumToo_NumbersCollideAcrossFoundries()
    {
        // Regression guard for the field report "waveguides detected as metal":
        // (11,0) is SiEPIC M1 in our table but an optical etch layer elsewhere —
        // the claim must stay a human-confirmed guess, never auto-applied.
        var library = Library(Cell(TopCell), Cell("trace", Rectangle(11, 0, 100, 2)));

        var suggestion = Suggest(library)
            .Single(s => s is { Layer: 11, Datatype: 0, Role: GdsLayerRole.Metal });

        suggestion.Confidence.ShouldBe(GdsSuggestionConfidence.Medium);
    }

    [Fact]
    public void PortLabelRestingOnALayer_ProvesItWaveguideHigh_AndVetoesMetalConvention()
    {
        // (11,0) is "SiEPIC M1" in the convention table — but the port label
        // rests on this layer's core, which is the stronger, content-based
        // evidence: the metal claim is vetoed, the waveguide claim is high.
        var library = Library(Cell(TopCell), Cell("dev",
            Rectangle(11, 0, 10, 0.5),
            new GdsText { Layer = 56, TextType = 0, Text = "o1", Position = new GdsPoint(0.5, 0.25) },
            new GdsText { Layer = 56, TextType = 0, Text = "o2", Position = new GdsPoint(9.5, 0.25) }));

        var suggestions = Suggest(library);

        suggestions.ShouldContain(s =>
            s.Layer == 11 && s.Datatype == 0 && s.Role == GdsLayerRole.Waveguide
            && s.Confidence == GdsSuggestionConfidence.High);
        suggestions.ShouldNotContain(s => s.Layer == 11 && s.Role == GdsLayerRole.Metal);
    }

    [Fact]
    public void ElectricalNamedLabel_ProvesNothingOptical_MetalConventionStaysMedium()
    {
        // "pad1" names a bond pad: it must never mark the layer it rests on as
        // optical — the bare metal convention claim remains a medium guess.
        var library = Library(Cell(TopCell), Cell("dev",
            Rectangle(11, 0, 10, 2),
            new GdsText { Layer = 56, TextType = 0, Text = "pad1", Position = new GdsPoint(5, 1) }));

        var suggestions = Suggest(library);

        suggestions.ShouldNotContain(s => s.Layer == 11 && s.Role == GdsLayerRole.Waveguide);
        suggestions.ShouldContain(s =>
            s.Layer == 11 && s.Role == GdsLayerRole.Metal
            && s.Confidence == GdsSuggestionConfidence.Medium);
    }

    [Fact]
    public void LabelAwayFromAnyShape_AttachesNothing()
    {
        var library = Library(Cell(TopCell), Cell("dev",
            Rectangle(11, 0, 10, 0.5),
            new GdsText { Layer = 56, TextType = 0, Text = "o1", Position = new GdsPoint(100, 100) }));

        var suggestions = Suggest(library);

        suggestions.ShouldNotContain(s =>
            s.Layer == 11 && s.Role == GdsLayerRole.Waveguide
            && s.Confidence == GdsSuggestionConfidence.High);
    }

    [Fact]
    public void LabelTouchingNestedShapes_AttachesToTheRouteLikeOne()
    {
        // keepout/bbox rectangle enclosing the core: the label rests on both,
        // but the frame covers the whole cell (background) and the core is
        // the route-like shape — only the core earns the attachment.
        var library = Library(Cell(TopCell), Cell("dev",
            Rectangle(111, 0, 20, 10),
            Rectangle(11, 0, 10, 0.5),
            new GdsText { Layer = 56, TextType = 0, Text = "o1", Position = new GdsPoint(5, 0.25) },
            new GdsText { Layer = 56, TextType = 0, Text = "o2", Position = new GdsPoint(9.5, 0.25) }));

        var suggestions = Suggest(library);

        suggestions.ShouldContain(s =>
            s.Layer == 11 && s.Role == GdsLayerRole.Waveguide
            && s.Confidence == GdsSuggestionConfidence.High);
        suggestions.ShouldNotContain(s => s.Layer == 111 && s.Role == GdsLayerRole.Waveguide);
    }

    [Fact]
    public void LabelOnStackedEqualSquares_PrefersTheDeviceContentLayer()
    {
        // An annotation backing square stacked exactly on the pin square:
        // same size, same spot — the layer that carries device geometry
        // throughout the file wins the tie, the annotation layer gets nothing.
        var library = Library(Cell(TopCell), Cell("dev",
            Rectangle(59, 0, 2, 2),      // annotation backing square at the pin
            Rectangle(11, 0, 2, 2),      // pin square on the waveguide layer
            Rectangle(11, 0, 10, 0.5),   // more device content on that layer
            new GdsText { Layer = 56, TextType = 0, Text = "o1", Position = new GdsPoint(0.5, 0.25) },
            new GdsText { Layer = 56, TextType = 0, Text = "o2", Position = new GdsPoint(1.5, 1.5) }));

        var suggestions = Suggest(library);

        suggestions.ShouldContain(s =>
            s.Layer == 11 && s.Role == GdsLayerRole.Waveguide
            && s.Confidence == GdsSuggestionConfidence.High);
        suggestions.ShouldNotContain(s => s.Layer == 59 && s.Role == GdsLayerRole.Waveguide);
    }

    [Fact]
    public void GhostOnlyTextLayer_YieldsNoPortLabelSuggestion()
    {
        // nazca stamps bbox anchors / parameter annotations on their own text
        // layers — a layer carrying ONLY helper labels is not a port candidate.
        var library = Library(Cell(TopCell), Cell("dev",
            new GdsText { Layer = 233, TextType = 0, Text = "tl", Position = new GdsPoint(0, 4) },
            new GdsText { Layer = 233, TextType = 0, Text = "R:0.0001", Position = new GdsPoint(5, 2) },
            Rectangle(1, 0, 10, 0.5)));

        Suggest(library).ShouldNotContain(s => s.Layer == 233 && s.Role == GdsLayerRole.PortLabels);
    }

    [Fact]
    public void LabelTouchingMarkerChevronAndPinSquare_AttachesToTheSquareNotTheMarkerLayer()
    {
        // The chevron is smaller than the pin square and sits right on the
        // label — but marker layers never take attachments.
        var library = Library(Cell(TopCell), Cell("dev",
            Chevron(232, 0.4, 0.25),
            Chevron(232, 0.4, 0.25, mirror: true),
            Rectangle(11, 0, 2, 2),
            Rectangle(11, 0, 10, 0.5),
            new GdsText { Layer = 56, TextType = 0, Text = "o1", Position = new GdsPoint(0.5, 0.25) },
            new GdsText { Layer = 56, TextType = 0, Text = "o2", Position = new GdsPoint(1.5, 1.5) }));

        var suggestions = Suggest(library);

        suggestions.ShouldContain(s =>
            s.Layer == 11 && s.Role == GdsLayerRole.Waveguide
            && s.Confidence == GdsSuggestionConfidence.High);
        suggestions.ShouldNotContain(s => s.Layer == 232 && s.Role == GdsLayerRole.Waveguide);
    }

    /// <summary>The nazca pin chevron (7 vertices, tip at the origin, ~0.35×0.5 µm).</summary>
    private static GdsPolygon Chevron(int layer, double tipX, double tipY, bool mirror = false)
    {
        (double X, double Y)[] shape =
            { (0.25, -0.25), (0, 0), (0.25, 0.25), (0.25, 0.125), (0.35, 0.125), (0.35, -0.125), (0.25, -0.125) };
        var sign = mirror ? -1 : 1;
        return new GdsPolygon
        {
            Layer = layer,
            DataType = 0,
            Points = shape.Select(p => new GdsPoint(tipX + sign * p.X, tipY + p.Y)).ToList(),
        };
    }

    [Fact]
    public void KnownPortConvention_RequiresSingleLineTexts()
    {
        var library = Library(Cell(TopCell, Text(1, 10, "meta\nblob")));

        Suggest(library).ShouldNotContain(s => s.Role == GdsLayerRole.PortLabels);
    }

    [Fact]
    public void TextBearingUnknownLayer_BecomesHighPortLabelCandidate_NamingCells()
    {
        var library = Library(Cell(TopCell), Cell("dev", Text(56, 0, "opt_1"), Text(56, 0, "opt_2")));

        var suggestion = Suggest(library)
            .Single(s => s is { Layer: 56, Datatype: 0, Role: GdsLayerRole.PortLabels });

        // single-line text labels are the port-label evidence itself — high:
        suggestion.Confidence.ShouldBe(GdsSuggestionConfidence.High);
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
    public void BoundaryTouchingPolygon_WithoutText_SuggestsPortLabelsLow()
    {
        // A small rectangle that shares one edge with its cell's bbox is a
        // plausible port marker even when it carries no text — surfaced as a
        // low-confidence, manually-accepted suggestion, never auto-applied. The
        // larger extent rectangle makes the marker touch only the left edge.
        var library = Library(Cell(TopCell), Cell("dev",
            Rectangle(111, 0, 10, 4),
            new GdsPolygon
            {
                Layer = 77,
                DataType = 0,
                Points = new[]
                {
                    new GdsPoint(0, 1), new GdsPoint(0.5, 1),
                    new GdsPoint(0.5, 2), new GdsPoint(0, 2), new GdsPoint(0, 1),
                },
            }));

        var suggestions = Suggest(library);

        suggestions.ShouldContain(s =>
            s.Layer == 77 && s.Datatype == 0 && s.Role == GdsLayerRole.PortLabels
            && s.Confidence == GdsSuggestionConfidence.Low);
    }

    [Fact]
    public void BoundaryTouchingPolygon_WithPortLikeText_TextSuggestionWins_NoDuplicate()
    {
        // Text evidence is stronger and already maps the layer to port labels;
        // the geometry-only hint must not produce a second suggestion.
        var library = Library(Cell(TopCell), Cell("dev",
            Rectangle(111, 0, 10, 4),
            new GdsPolygon
            {
                Layer = 77,
                DataType = 0,
                Points = new[]
                {
                    new GdsPoint(0, 1), new GdsPoint(0.5, 1),
                    new GdsPoint(0.5, 2), new GdsPoint(0, 2), new GdsPoint(0, 1),
                },
            },
            Text(77, 0, "o1")));

        var suggestions = Suggest(library);

        suggestions.ShouldContain(s =>
            s.Layer == 77 && s.Datatype == 0 && s.Role == GdsLayerRole.PortLabels
            && s.Confidence == GdsSuggestionConfidence.High);
        suggestions.ShouldNotContain(s =>
            s.Layer == 77 && s.Role == GdsLayerRole.PortLabels
            && s.Confidence == GdsSuggestionConfidence.Low);
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
