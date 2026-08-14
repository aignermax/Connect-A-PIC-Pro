using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Routing.InterconnectRouting;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Routing.InterconnectRouting;

/// <summary>
/// Tests for the direct/S-bend-first routing policy (issue #860): the styled geometry is
/// tried before A*, verified against the same obstacle grid, and A* only runs as the
/// obstacle-avoidance fallback.
/// </summary>
public class DirectRouteFirstPolicyTests
{
    private const double DefaultBendRadius = 10.0;

    // ----- Candidate construction (style choice per pin geometry) -----

    [Fact]
    public void TryBuildCandidate_CoaxialFacingPins_ReturnsSingleStraight()
    {
        var (start, end) = FacingPins(startX: 0, startY: 0, endX: 100, endY: 0);

        var path = DirectRouteFirstPolicy.TryBuildCandidate(start, end, DefaultBendRadius);

        path.ShouldNotBeNull();
        path.Segments.ShouldHaveSingleItem();
        path.Segments[0].ShouldBeOfType<StraightSegment>();
        AssertEndpointsMatchPins(path, start, end);
    }

    [Fact]
    public void TryBuildCandidate_SmallParallelOffset_ReturnsArcSBend()
    {
        var (start, end) = FacingPins(startX: 0, startY: 0, endX: 150, endY: 40);

        var path = DirectRouteFirstPolicy.TryBuildCandidate(start, end, DefaultBendRadius);

        path.ShouldNotBeNull();
        path.IsValid.ShouldBeTrue();
        var bends = path.Segments.OfType<BendSegment>().ToList();
        bends.Count.ShouldBe(2, "a parallel offset should produce the two-arc S");
        bends.ShouldAllBe(b => b.RadiusMicrometers >= DefaultBendRadius - 1e-3);
        AssertEndpointsMatchPins(path, start, end);
    }

    [Fact]
    public void TryBuildCandidate_LargeOffsetWhereArcsCannotHonorFloor_ReturnsSmoothPolyline()
    {
        // 80µm run with 50µm lateral shift at a 25µm floor: the arc-S cannot fit that
        // radius (max fitting radius ≈ 24µm), but the sine polyline's gentler curvature
        // (min radius ≈ 27µm) still honors it.
        var (start, end) = FacingPins(startX: 0, startY: 0, endX: 80, endY: 50);

        var path = DirectRouteFirstPolicy.TryBuildCandidate(start, end, minBendRadiusMicrometers: 25.0);

        path.ShouldNotBeNull();
        path.IsValid.ShouldBeTrue();
        path.Segments.OfType<BendSegment>().ShouldBeEmpty("arcs cannot honor the floor here");
        path.Segments.OfType<StraightSegment>().Count().ShouldBeGreaterThan(4,
            "the sine S-bend is a sampled polyline of straight chords");
        AssertEndpointsMatchPins(path, start, end);
    }

    [Fact]
    public void TryBuildCandidate_PerpendicularPins_ReturnsArcRoute()
    {
        var start = Pin(x: 0, y: 0, angleDegrees: 0);      // heading +X
        var end = Pin(x: 100, y: 100, angleDegrees: 270);   // arrival heading +Y

        var path = DirectRouteFirstPolicy.TryBuildCandidate(start, end, DefaultBendRadius);

        path.ShouldNotBeNull();
        path.IsValid.ShouldBeTrue();
        var bend = path.Segments.OfType<BendSegment>().ShouldHaveSingleItem();
        bend.RadiusMicrometers.ShouldBeGreaterThanOrEqualTo(DefaultBendRadius - 1e-3);
        AssertEndpointsMatchPins(path, start, end);
    }

    [Fact]
    public void TryBuildCandidate_EndPinBehindStart_ReturnsNull()
    {
        // The end pin lies BEHIND the start pin's heading: no styled curve can leave the
        // start pin along its direction, so the policy defers to A*.
        var start = Pin(x: 0, y: 0, angleDegrees: 0);   // heading +X
        var end = Pin(x: -100, y: 0, angleDegrees: 0);  // arrival heading -X, behind start

        DirectRouteFirstPolicy.TryBuildCandidate(start, end, DefaultBendRadius)
            .ShouldBeNull();
    }

    [Fact]
    public void TryBuildCandidate_RadiusFloorUnreachable_ReturnsNull()
    {
        // Tight parallel offset: neither the arc-S nor the sine curvature can satisfy
        // a 10µm floor over a 20µm run with 15µm lateral shift.
        var (start, end) = FacingPins(startX: 0, startY: 0, endX: 20, endY: 15);

        DirectRouteFirstPolicy.TryBuildCandidate(start, end, DefaultBendRadius)
            .ShouldBeNull();
    }

    // ----- Router integration: direct first, A* only on obstruction -----

    [Fact]
    public void Route_ClearLine_ReturnsDirectStyledRoute()
    {
        var router = CreateRouter();
        router.InitializePathfindingGrid(-50, -50, 300, 200, Array.Empty<Component>());
        var (start, end) = FacingPins(startX: 0, startY: 25, endX: 200, endY: 75);

        var path = router.Route(start, end);

        path.IsDirectStyledRoute.ShouldBeTrue("a clear line must be routed directly, not by A*");
        path.IsValid.ShouldBeTrue();
        path.IsBlockedFallback.ShouldBeFalse();
        path.DebugGridPath.ShouldBeNull("the direct route never ran the grid search");
        AssertEndpointsMatchPins(path, start, end);
    }

    [Fact]
    public void Route_ObstacleBlocksStyledPath_FallsBackToAStar()
    {
        var router = CreateRouter();
        var obstacle = CreateBlockComponent(x: 100, y: -50, width: 50, height: 150);
        router.InitializePathfindingGrid(-50, -100, 300, 200, new[] { obstacle });
        var (start, end) = FacingPins(startX: 0, startY: 25, endX: 250, endY: 25);

        var path = router.Route(start, end);

        path.IsDirectStyledRoute.ShouldBeFalse("a blocked styled path must fall back to A*");
        path.IsBlockedFallback.ShouldBeFalse("A* should route around the obstacle");
        path.IsValid.ShouldBeTrue();
        router.IsPathBlocked(path.Segments).ShouldBeFalse();
    }

    [Fact]
    public void Route_SiblingWaveguideBlocksStyledPath_FallsBackToAStar()
    {
        // The obstacle check must use the SAME grid A* uses — including registered
        // sibling waveguides, not only component bodies.
        var router = CreateRouter();
        router.InitializePathfindingGrid(-50, -100, 300, 200, Array.Empty<Component>());
        var sibling = new List<PathSegment>
        {
            new StraightSegment(125, -80, 125, 180, 90)
        };
        router.PathfindingGrid!.AddWaveguideObstacle(Guid.NewGuid(), sibling, 4.0);
        var (start, end) = FacingPins(startX: 0, startY: 25, endX: 250, endY: 25);

        var path = router.Route(start, end);

        path.IsDirectStyledRoute.ShouldBeFalse("a styled path crossing a sibling must fall back to A*");
    }

    [Fact]
    public void Route_PolicyDisabled_NeverReturnsDirectRoute()
    {
        var router = CreateRouter();
        router.PreferDirectStyledRoutes = false;
        router.InitializePathfindingGrid(-50, -50, 300, 200, Array.Empty<Component>());
        var (start, end) = FacingPins(startX: 0, startY: 25, endX: 200, endY: 25);

        var path = router.Route(start, end);

        path.IsDirectStyledRoute.ShouldBeFalse();
        path.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Route_EndpointComponentsOwnPaddedCells_DoNotDeferToAStar()
    {
        // The styled candidate leaves/enters the pin THROUGH the endpoint
        // component's own padded obstacle cells — the same allowance A* gets
        // via the pin corridor. Field report on a dense array import: only
        // 10–20 % of clear channels got the styled route without this.
        var router = CreateRouter();
        var startComp = CreateBlockComponent(x: 0, y: 0, width: 20, height: 30);
        var endComp = CreateBlockComponent(x: 180, y: 40, width: 20, height: 30);
        router.InitializePathfindingGrid(-60, -60, 340, 220, new[] { startComp, endComp });

        var start = new PhysicalPin
        {
            Name = "out", OffsetXMicrometers = 20, OffsetYMicrometers = 15,
            AngleDegrees = 0, ParentComponent = startComp,
        };
        var end = new PhysicalPin
        {
            Name = "in", OffsetXMicrometers = 0, OffsetYMicrometers = 15,
            AngleDegrees = 180, ParentComponent = endComp,
        };

        var path = router.Route(start, end);

        path.IsDirectStyledRoute.ShouldBeTrue(
            "exiting through the endpoint's own padded cells must not defer the styled route to A*");
        path.IsValid.ShouldBeTrue();
        AssertEndpointsMatchPins(path, start, end);
    }

    [Fact]
    public void Route_DirectPathThroughStartBodyBeyondCorridor_RejectedHonestlyBlocked()
    {
        // The pin-corridor allowance must END at the corridor (3·radius × radius,
        // same as A* clears) — a styled path that keeps running through the start
        // component's body BEYOND the corridor must not be accepted. Here the pin
        // sits inside a thin, wide body deeper than the corridor reaches, so even
        // A* cannot get there: the connection must be HONESTLY flagged blocked,
        // never silently drawn through the body as if everything was fine.
        var router = CreateRouter();
        var startComp = CreateBlockComponent(x: 0, y: 8, width: 100, height: 4);
        var endComp = CreateBlockComponent(x: 200, y: 10, width: 20, height: 30);
        router.InitializePathfindingGrid(-60, -60, 340, 160, new[] { startComp, endComp });

        var start = new PhysicalPin
        {
            Name = "out", OffsetXMicrometers = 20, OffsetYMicrometers = 2,
            AngleDegrees = 0, ParentComponent = startComp,
        };
        var end = new PhysicalPin
        {
            Name = "in", OffsetXMicrometers = 0, OffsetYMicrometers = 15,
            AngleDegrees = 180, ParentComponent = endComp,
        };

        var path = router.Route(start, end);

        path.IsDirectStyledRoute.ShouldBeFalse(
            "a styled path through the component body (beyond the pin corridor) must be rejected");
        path.IsBlockedFallback.ShouldBeTrue(
            "the pin is buried deeper than the A* pin corridor reaches — honestly flagged");
    }

    [Fact]
    public void Route_DirectPathThroughEndBodyBeyondCorridor_RejectedHonestlyBlocked()
    {
        // Mirror image on the target side: the collinear straight would run
        // through the END component's body to reach a pin seated past its corridor.
        var router = CreateRouter();
        var startComp = CreateBlockComponent(x: 0, y: 0, width: 20, height: 30);
        var endComp = CreateBlockComponent(x: 100, y: 8, width: 100, height: 4);
        router.InitializePathfindingGrid(-60, -60, 340, 160, new[] { startComp, endComp });

        var start = new PhysicalPin
        {
            Name = "out", OffsetXMicrometers = 20, OffsetYMicrometers = 15,
            AngleDegrees = 0, ParentComponent = startComp,
        };
        var end = new PhysicalPin
        {
            Name = "in", OffsetXMicrometers = 50, OffsetYMicrometers = 2,
            AngleDegrees = 180, ParentComponent = endComp,
        };

        var path = router.Route(start, end);

        path.IsDirectStyledRoute.ShouldBeFalse(
            "a styled path through the target component's body must be rejected");
        path.IsBlockedFallback.ShouldBeTrue(
            "the pin is buried deeper than the A* pin corridor reaches — honestly flagged");
    }

    [Fact]
    public void Route_WithoutGrid_StillPrefersDirectRoute()
    {
        var router = CreateRouter();
        var (start, end) = FacingPins(startX: 0, startY: 0, endX: 150, endY: 40);

        var path = router.Route(start, end);

        path.IsDirectStyledRoute.ShouldBeTrue();
        AssertEndpointsMatchPins(path, start, end);
    }

    [Fact]
    public void TranslatedCopy_PreservesDirectStyledFlag()
    {
        var path = new RoutedPath { IsDirectStyledRoute = true };
        path.Segments.Add(new StraightSegment(0, 0, 10, 0, 0));

        path.TranslatedCopy(5, 5).IsDirectStyledRoute.ShouldBeTrue();
        path.DeepCopy().IsDirectStyledRoute.ShouldBeTrue();
    }

    // ----- Helpers -----

    private static WaveguideRouter CreateRouter() => new()
    {
        MinBendRadiusMicrometers = DefaultBendRadius,
        MinWaveguideSpacingMicrometers = 2.0
    };

    /// <summary>A free-standing pin at an absolute position (zero-size parent component).</summary>
    private static PhysicalPin Pin(double x, double y, double angleDegrees)
    {
        var component = CreateBlockComponent(x, y, width: 0, height: 0);
        return new PhysicalPin
        {
            Name = "pin",
            OffsetXMicrometers = 0,
            OffsetYMicrometers = 0,
            AngleDegrees = angleDegrees,
            ParentComponent = component
        };
    }

    /// <summary>A pair of pins facing each other along +X (start heads right, end receives).</summary>
    private static (PhysicalPin Start, PhysicalPin End) FacingPins(
        double startX, double startY, double endX, double endY) =>
        (Pin(startX, startY, angleDegrees: 0), Pin(endX, endY, angleDegrees: 180));

    private static void AssertEndpointsMatchPins(RoutedPath path, PhysicalPin start, PhysicalPin end)
    {
        var (sx, sy) = start.GetAbsolutePosition();
        var (ex, ey) = end.GetAbsolutePosition();
        path.Segments[0].StartPoint.X.ShouldBe(sx, 0.01);
        path.Segments[0].StartPoint.Y.ShouldBe(sy, 0.01);
        path.Segments[^1].EndPoint.X.ShouldBe(ex, 0.01);
        path.Segments[^1].EndPoint.Y.ShouldBe(ey, 0.01);
    }

    private static Component CreateBlockComponent(double x, double y, double width, double height)
    {
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin>());
        var component = new Component(
            laserWaveLengthToSMatrixMap: new Dictionary<int, SMatrix>(),
            sliders: new List<Slider>(),
            nazcaFunctionName: "test",
            nazcaFunctionParams: "",
            parts: parts,
            typeNumber: 0,
            identifier: $"Block_{x}_{y}",
            rotationCounterClock: DiscreteRotation.R0)
        {
            WidthMicrometers = width,
            HeightMicrometers = height,
            PhysicalX = x,
            PhysicalY = y
        };
        return component;
    }
}
