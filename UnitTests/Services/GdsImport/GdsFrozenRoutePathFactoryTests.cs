using CAP.Avalonia.Services.GdsImport;
using CAP_DataAccess.Import.Gds;
using Shouldly;
using Xunit;

namespace UnitTests.Services.GdsImport;

/// <summary>
/// Tests for <see cref="GdsFrozenRoutePathFactory.CreateConnectionRoute"/>: the
/// traced cached route for route-derived import connections — pin anchoring
/// (what the frozen-route keep-checks require), ring tracing, chaining over
/// multi-polygon networks, and the fallback-to-routing null cases.
/// </summary>
public class GdsFrozenRoutePathFactoryTests
{
    /// <summary>Closed axis-aligned rectangle ring (5 points, first repeated), like the importer produces.</summary>
    private static GdsOutlinePolygon Rect(double x1, double y1, double x2, double y2) =>
        new()
        {
            Layer = 1,
            DataType = 0,
            Points = new[]
            {
                new GdsOutlinePoint(x1, y1),
                new GdsOutlinePoint(x2, y1),
                new GdsOutlinePoint(x2, y2),
                new GdsOutlinePoint(x1, y2),
                new GdsOutlinePoint(x1, y1),
            },
        };

    [Fact]
    public void CreateConnectionRoute_SingleStripe_AnchorsAtPinsAndTracesTheOutline()
    {
        // The 10 µm bridge between wgA.out (10, 2) and wgB.in (20, 2).
        var route = GdsFrozenRoutePathFactory.CreateConnectionRoute(
            new[] { Rect(10, 1.75, 20, 2.25) }, (10, 2), (20, 2));

        route.ShouldNotBeNull();
        route.IsValid.ShouldBeTrue("consecutive segments must connect for the route to be kept");
        var first = route.Segments[0];
        var last = route.Segments[^1];
        first.StartPoint.ShouldBe((10.0, 2.0), "the frozen keep-check needs the exact start pin");
        last.EndPoint.ShouldBe((20.0, 2.0), "the frozen keep-check needs the exact end pin");

        // The ring contributes its four edges (the closing repeat is not doubled).
        route.Segments.Count.ShouldBe(6, "two pin whiskers + four rectangle edges");
        HasSegment(route, 10, 1.75, 20, 1.75).ShouldBeTrue("the bottom edge is traced");
        HasSegment(route, 20, 2.25, 10, 2.25).ShouldBeTrue("the top edge is traced");
    }

    [Fact]
    public void CreateConnectionRoute_AppliesOriginOffsetToPolygonGeometryOnly()
    {
        var route = GdsFrozenRoutePathFactory.CreateConnectionRoute(
            new[] { Rect(10, 1.75, 20, 2.25) }, (60, 52), (70, 52), offsetXUm: 50, offsetYUm: 50);

        route.ShouldNotBeNull();
        route.IsValid.ShouldBeTrue();
        route.Segments[0].StartPoint.ShouldBe((60.0, 52.0), "pin anchors are canvas-absolute already");
        route.Segments[^1].EndPoint.ShouldBe((70.0, 52.0));
        HasSegment(route, 60, 51.75, 70, 51.75).ShouldBeTrue(
            "the polygon trace is shifted into canvas space by the offset");
    }

    [Fact]
    public void CreateConnectionRoute_MultiPolygonChain_TracesEveryRing()
    {
        // A two-polygon chain (overlapping halves, as a flattened route emits).
        var route = GdsFrozenRoutePathFactory.CreateConnectionRoute(
            new[] { Rect(10, 1.75, 15, 2.25), Rect(14.9, 1.75, 20, 2.25) }, (10, 2), (20, 2));

        route.ShouldNotBeNull();
        route.IsValid.ShouldBeTrue();
        route.Segments[0].StartPoint.ShouldBe((10.0, 2.0));
        route.Segments[^1].EndPoint.ShouldBe((20.0, 2.0));
        // Both rings traced: an edge unique to each half must appear.
        HasSegment(route, 10, 1.75, 15, 1.75).ShouldBeTrue("the first half's ring is traced");
        HasSegment(route, 20, 2.25, 14.9, 2.25).ShouldBeTrue("the second half's ring is traced");
    }

    /// <summary>True when the route contains a straight segment with exactly these endpoints (in this order).</summary>
    private static bool HasSegment(
        CAP_Core.Routing.RoutedPath route, double startX, double startY, double endX, double endY) =>
        route.Segments.Any(s =>
            s.StartPoint.X == startX && s.StartPoint.Y == startY
            && s.EndPoint.X == endX && s.EndPoint.Y == endY);

    [Fact]
    public void CreateConnectionRoute_NoUsableOutline_ReturnsNull()
    {
        GdsFrozenRoutePathFactory.CreateConnectionRoute(
                Array.Empty<GdsOutlinePolygon>(), (0, 0), (10, 0))
            .ShouldBeNull("nothing to trace — the caller falls back to routing");

        var pointLike = new GdsOutlinePolygon
        {
            Layer = 1,
            DataType = 0,
            Points = new[] { new GdsOutlinePoint(5, 5), new GdsOutlinePoint(5, 5) },
        };
        GdsFrozenRoutePathFactory.CreateConnectionRoute(new[] { pointLike }, (0, 0), (10, 0))
            .ShouldBeNull("a degenerate (point-like) ring has no outline to trace");
    }
}
