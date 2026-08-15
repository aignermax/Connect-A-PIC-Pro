using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Routing.InterconnectRouting;

/// <summary>
/// Regression tests for issue #874: on dense fan-out arrays the direct styled candidate
/// must be judged against the EXACT geometry of registered sibling routes, not against
/// their rasterized grid cells. The cell approximation (cell size + half the obstacle
/// width) read parallel neighbors as blockers, so after the first few routes every later
/// connection degraded to a red blocked fallback even though its S-bend was clean.
/// </summary>
public class DirectRouteSiblingClearanceTests
{
    private const double DefaultBendRadius = 10.0;
    private const double SiblingObstacleWidth = 4.0;

    [Fact]
    public void Route_ParallelSiblingWithClearance_StillRoutesDirect()
    {
        // A registered sibling running parallel 6 µm away: geometrically clear
        // (spacing 2 µm), but its rasterized cells (4 µm cells + 2 µm half width)
        // overlap the candidate — the old cell test rejected this.
        var router = CreateRouter();
        router.InitializePathfindingGrid(-50, -50, 300, 200, Array.Empty<Component>());
        router.PathfindingGrid!.AddWaveguideObstacle(Guid.NewGuid(), new List<PathSegment>
        {
            new StraightSegment(-40, 31, 290, 31, 0)
        }, SiblingObstacleWidth);
        var (start, end) = FacingPins(startX: 0, startY: 25, endX: 250, endY: 25);

        var path = router.Route(start, end);

        path.IsDirectStyledRoute.ShouldBeTrue(
            "a parallel sibling with real clearance must not defer the styled route to A*");
        path.IsBlockedFallback.ShouldBeFalse();
    }

    [Fact]
    public void Route_SiblingCrossingCandidate_DefersToAStar()
    {
        var router = CreateRouter();
        router.InitializePathfindingGrid(-50, -100, 300, 200, Array.Empty<Component>());
        router.PathfindingGrid!.AddWaveguideObstacle(Guid.NewGuid(), new List<PathSegment>
        {
            new StraightSegment(125, -80, 125, 180, 90)
        }, SiblingObstacleWidth);
        var (start, end) = FacingPins(startX: 0, startY: 25, endX: 250, endY: 25);

        var path = router.Route(start, end);

        path.IsDirectStyledRoute.ShouldBeFalse(
            "a styled path genuinely crossing a sibling must fall back to A*");
    }

    [Fact]
    public void Route_SiblingTooCloseMidPath_DefersToAStar()
    {
        // Sibling runs 1 µm from the candidate's mid-path (beyond the pin fan-out
        // exemption of 3·radius = 30 µm from both pins): violates the 2 µm spacing.
        var router = CreateRouter();
        router.InitializePathfindingGrid(-50, -50, 300, 200, Array.Empty<Component>());
        router.PathfindingGrid!.AddWaveguideObstacle(Guid.NewGuid(), new List<PathSegment>
        {
            new StraightSegment(100, 26, 150, 26, 0)
        }, SiblingObstacleWidth);
        var (start, end) = FacingPins(startX: 0, startY: 25, endX: 250, endY: 25);

        var path = router.Route(start, end);

        path.IsDirectStyledRoute.ShouldBeFalse(
            "a sibling closer than the minimum spacing must defer the styled route to A*");
    }

    [Fact]
    public void Route_OwnStaleRegistrationSameEndpoints_IsIgnored()
    {
        // A registered obstacle between the SAME pin positions is this connection's own
        // previous route (e.g. a re-route without a prior grid clear) — it must never
        // block its own replacement.
        var router = CreateRouter();
        router.InitializePathfindingGrid(-50, -50, 300, 200, Array.Empty<Component>());
        router.PathfindingGrid!.AddWaveguideObstacle(Guid.NewGuid(), new List<PathSegment>
        {
            new StraightSegment(0, 25, 250, 25, 0)
        }, SiblingObstacleWidth);
        var (start, end) = FacingPins(startX: 0, startY: 25, endX: 250, endY: 25);

        var path = router.Route(start, end);

        path.IsDirectStyledRoute.ShouldBeTrue(
            "the connection's own stale registration must not block its re-route");
    }

    [Fact]
    public void Route_SiblingNearPinsWithinFanoutExemption_StillRoutesDirect()
    {
        // Fan-out pattern: the neighbor's route starts 1.5 µm away (sub-spacing pin
        // pitch) and fans out within the pin zone. Proximity inside the pin fan-out
        // zones (3·radius = 30 µm around the candidate's pins) is dictated by the
        // fixed pitch and must be tolerated; beyond them the sibling is clear.
        var router = CreateRouter();
        router.InitializePathfindingGrid(-50, -50, 400, 200, Array.Empty<Component>());
        router.PathfindingGrid!.AddWaveguideObstacle(Guid.NewGuid(), new List<PathSegment>
        {
            new StraightSegment(0, 26.5, 20, 26.5, 0),
            new StraightSegment(20, 26.5, 50, 32, 10.4),
            new StraightSegment(50, 32, 250, 32, 0)
        }, SiblingObstacleWidth);
        var (start, end) = FacingPins(startX: 0, startY: 25, endX: 250, endY: 25);

        var path = router.Route(start, end);

        path.IsDirectStyledRoute.ShouldBeTrue(
            "pin-pitch proximity to a fan-out sibling must not defer the styled route");
    }

    [Fact]
    public void Route_SiblingTouchingCandidateInsideFanoutZone_DefersToAStar()
    {
        // The fan-out exemption tolerates sub-spacing proximity near the pins, but never
        // CONTACT: a sibling hugging the candidate closer than one waveguide width means
        // the drawn cores merge — the exported GDS polygons touch and a re-import can no
        // longer disentangle the two routes into separate connections.
        var router = CreateRouter();
        router.InitializePathfindingGrid(-50, -50, 300, 200, Array.Empty<Component>());
        router.PathfindingGrid!.AddWaveguideObstacle(Guid.NewGuid(), new List<PathSegment>
        {
            new StraightSegment(5, 25.5, 25, 25.5, 0)
        }, SiblingObstacleWidth);
        var (start, end) = FacingPins(startX: 0, startY: 25, endX: 250, endY: 25);

        var path = router.Route(start, end);

        path.IsDirectStyledRoute.ShouldBeFalse(
            "a sibling touching the candidate must defer the styled route to A*, " +
            "even inside the pin fan-out exemption zone");
    }

    [Fact]
    public void Route_DenseFanoutArray_AllConnectionsRouteDirectWithoutBlockedFallbacks()
    {
        // Field scenario (issue #874): a dense array of parallel channels routed
        // sequentially, each registered as an obstacle before the next routes. With the
        // cell-based sibling test only the first few routed directly; all must now.
        const int channelCount = 16;
        const double pitch = 8.0;

        var router = CreateRouter();
        router.InitializePathfindingGrid(-50, -50, 400, 300, Array.Empty<Component>());

        int directCount = 0;
        for (int i = 0; i < channelCount; i++)
        {
            double y = i * pitch;
            // Slight lateral offset per channel so each route is a genuine S-bend.
            var (start, end) = FacingPins(startX: 0, startY: y, endX: 300, endY: y + 4);

            var path = router.Route(start, end);
            path.IsBlockedFallback.ShouldBeFalse($"channel {i} must not degrade to red");
            if (path.IsDirectStyledRoute) directCount++;

            router.PathfindingGrid!.AddWaveguideObstacle(
                Guid.NewGuid(), path.Segments, SiblingObstacleWidth);
        }

        directCount.ShouldBe(channelCount,
            "every channel of the dense array must route as a clean direct S-bend");
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
        var component = CreateBlockComponent(x, y);
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

    private static Component CreateBlockComponent(double x, double y)
    {
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin>());
        return new Component(
            laserWaveLengthToSMatrixMap: new Dictionary<int, SMatrix>(),
            sliders: new List<Slider>(),
            nazcaFunctionName: "test",
            nazcaFunctionParams: "",
            parts: parts,
            typeNumber: 0,
            identifier: $"Block_{x}_{y}",
            rotationCounterClock: DiscreteRotation.R0)
        {
            WidthMicrometers = 0,
            HeightMicrometers = 0,
            PhysicalX = x,
            PhysicalY = y
        };
    }
}
