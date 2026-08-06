using Avalonia;
using CAP.Avalonia.Controls.Rendering;
using CAP_Core.Components.Core;
using CAP_Core.Routing;
using Shouldly;
using Xunit;

namespace UnitTests.Rendering;

/// <summary>
/// Unit tests for the viewport-culling and level-of-detail helpers used by the
/// canvas renderers on large imported designs: frozen-path bounding boxes (with
/// translation-aware caching), the LOD pixel threshold, and viewport inflation.
/// Pure-math tests only; the draw calls themselves need a render platform.
/// </summary>
public class RenderCullingTests
{
    private const double Tolerance = 1e-9;

    private static FrozenWaveguidePath StraightPath(double x0, double y0, double x1, double y1)
    {
        var frozen = new FrozenWaveguidePath { Path = new RoutedPath() };
        frozen.Path.Segments.Add(new StraightSegment(x0, y0, x1, y1, 0));
        return frozen;
    }

    // ── ComputeSegmentBounds ────────────────────────────────────────────────

    [Fact]
    public void ComputeSegmentBounds_StraightSegment_ReturnsEndpointBoundingBox()
    {
        var segments = new PathSegment[] { new StraightSegment(2, 3, 8, 7, 0) };

        var bounds = RenderCulling.ComputeSegmentBounds(segments);

        bounds.X.ShouldBe(2.0, Tolerance);
        bounds.Y.ShouldBe(3.0, Tolerance);
        bounds.Width.ShouldBe(6.0, Tolerance);
        bounds.Height.ShouldBe(4.0, Tolerance);
    }

    [Fact]
    public void ComputeSegmentBounds_BendSegment_IncludesFullCircleAroundCenter()
    {
        // The conservative bend box is centre ± radius, so no arc orientation can
        // ever be culled while still visible.
        var segments = new PathSegment[]
        {
            new BendSegment(centerX: 10, centerY: 10, radius: 5, startAngle: 0, sweepAngle: 90)
        };

        var bounds = RenderCulling.ComputeSegmentBounds(segments);

        bounds.X.ShouldBe(5.0, Tolerance);
        bounds.Y.ShouldBe(5.0, Tolerance);
        bounds.Right.ShouldBe(15.0, Tolerance);
        bounds.Bottom.ShouldBe(15.0, Tolerance);
    }

    [Fact]
    public void ComputeSegmentBounds_MultipleSegments_ReturnsUnion()
    {
        var segments = new PathSegment[]
        {
            new StraightSegment(0, 0, 10, 0, 0),
            new StraightSegment(10, 0, 10, 20, 90)
        };

        var bounds = RenderCulling.ComputeSegmentBounds(segments);

        bounds.X.ShouldBe(0.0, Tolerance);
        bounds.Y.ShouldBe(0.0, Tolerance);
        bounds.Right.ShouldBe(10.0, Tolerance);
        bounds.Bottom.ShouldBe(20.0, Tolerance);
    }

    // ── GetFrozenPathBounds ─────────────────────────────────────────────────

    [Fact]
    public void GetFrozenPathBounds_PathWithoutSegments_ReturnsNull()
    {
        RenderCulling.GetFrozenPathBounds(new FrozenWaveguidePath()).ShouldBeNull();
        RenderCulling.GetFrozenPathBounds(new FrozenWaveguidePath { Path = new RoutedPath() }).ShouldBeNull();
    }

    [Fact]
    public void GetFrozenPathBounds_PathAnchoredAtOrigin_ReturnsRealBoundsNotCacheDefault()
    {
        // An anchor of (0,0) must not be confused with an empty cache slot.
        var frozen = StraightPath(0, 0, 5, 5);

        var bounds = RenderCulling.GetFrozenPathBounds(frozen);

        bounds.ShouldNotBeNull();
        bounds!.Value.Width.ShouldBe(5.0, Tolerance);
        bounds.Value.Height.ShouldBe(5.0, Tolerance);
    }

    [Fact]
    public void GetFrozenPathBounds_RepeatedCalls_ReturnStableBounds()
    {
        var frozen = StraightPath(1, 2, 11, 22);

        var first = RenderCulling.GetFrozenPathBounds(frozen);
        var second = RenderCulling.GetFrozenPathBounds(frozen);

        second.ShouldBe(first);
        first!.Value.X.ShouldBe(1.0, Tolerance);
        first.Value.Y.ShouldBe(2.0, Tolerance);
    }

    [Fact]
    public void GetFrozenPathBounds_AfterTranslateBy_ReturnsTranslatedBounds()
    {
        // Group moves translate frozen segments in place, so the cached bounds must
        // follow the path instead of serving the pre-move box.
        var frozen = StraightPath(0, 0, 10, 10);
        var before = RenderCulling.GetFrozenPathBounds(frozen);

        frozen.TranslateBy(100, 50);
        var after = RenderCulling.GetFrozenPathBounds(frozen);

        before!.Value.X.ShouldBe(0.0, Tolerance);
        after!.Value.X.ShouldBe(100.0, Tolerance);
        after.Value.Y.ShouldBe(50.0, Tolerance);
        after.Value.Width.ShouldBe(10.0, Tolerance);
        after.Value.Height.ShouldBe(10.0, Tolerance);
    }

    // ── IsBelowLodThreshold ─────────────────────────────────────────────────

    [Theory]
    [InlineData(10, 10, 1.0, false)]   // typical component at typical zoom: full detail
    [InlineData(100, 1, 1.0, false)]   // long thin waveguide: larger dimension governs
    [InlineData(10, 2, 0.3, true)]     // 3 px on screen: body-only
    [InlineData(4, 4, 1.0, false)]     // exactly at the threshold: still full detail
    [InlineData(10, 10, 0.0, false)]   // degenerate zoom falls back to 1.0
    public void IsBelowLodThreshold_ComparesLargerDimensionInScreenPixels(
        double width, double height, double zoom, bool expected)
    {
        RenderCulling.IsBelowLodThreshold(width, height, zoom).ShouldBe(expected);
    }

    // ── InflateForCulling ───────────────────────────────────────────────────

    [Fact]
    public void InflateForCulling_GrowsViewportByMarginInWorldUnits()
    {
        var viewport = new Rect(0, 0, 100, 100);

        var inflated = RenderCulling.InflateForCulling(viewport, zoom: 2.0);

        double expectedMargin = RenderCulling.CullMarginScreenPx / 2.0;
        inflated.X.ShouldBe(-expectedMargin, Tolerance);
        inflated.Y.ShouldBe(-expectedMargin, Tolerance);
        inflated.Width.ShouldBe(100 + 2 * expectedMargin, Tolerance);
        inflated.Height.ShouldBe(100 + 2 * expectedMargin, Tolerance);
    }

    [Fact]
    public void InflateForCulling_PartiallyVisibleItemStaysInside()
    {
        // An item just off the left edge must still intersect the inflated rect so
        // its on-screen half keeps rendering.
        var viewport = new Rect(0, 0, 100, 100);
        var itemJustOffscreen = new Rect(-10, 40, 8, 8);

        var inflated = RenderCulling.InflateForCulling(viewport, zoom: 1.0);

        inflated.Intersects(itemJustOffscreen).ShouldBeTrue();
    }
}
