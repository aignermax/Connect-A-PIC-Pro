using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Routing.InterconnectRouting;
using CAP_Core.Routing.InterconnectRouting.SegmentShift;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Routing;

/// <summary>
/// End-to-end tests for issue #805 through <see cref="WaveguideConnectionManager"/>:
/// dragging BOTH connected components together must translate the frozen route (manual
/// bend radii survive), while single moves and collisions with a third component still
/// fall back to the normal re-route path. The undo round-trip is the same joint move in
/// reverse, so validity must hold in both directions.
/// </summary>
public class JointMoveRouteTranslationIntegrationTests
{
    private static readonly double[] RadiusCandidates = { 40.0, 25.0, 15.0, 12.0 };
    private const double JointMoveDelta = 60.0;
    private const double CoordinateTolerance = 1e-9;

    [Fact]
    public void JointMove_FrozenRouteWithEditedRadius_IsTranslatedNotRerouted()
    {
        var (manager, router, connection, components) = RouteAcrossCorner();
        double appliedRadius = ApplyRadiusOverride(connection);
        var originalPoints = SegmentPointsOf(connection);

        MoveBothComponents(components, JointMoveDelta, JointMoveDelta);
        router.PathfindingGrid!.RebuildFromComponents(components);
        manager.RecalculateAllTransmissions();

        connection.IsRouteFrozen.ShouldBeTrue("a joint move must not unfreeze the route");
        connection.BendRadiusOverrides.ShouldContainKeyAndValue(0, appliedRadius);
        AssertTranslatedBy(connection, originalPoints, JointMoveDelta, JointMoveDelta);
    }

    [Fact]
    public void SingleMove_FrozenRouteWithEditedRadius_StillReroutesAndDropsEdit()
    {
        var (manager, router, connection, components) = RouteAcrossCorner();
        ApplyRadiusOverride(connection);

        components[1].PhysicalX += JointMoveDelta;
        router.PathfindingGrid!.RebuildFromComponents(components);
        manager.RecalculateAllTransmissions();

        connection.IsRouteFrozen.ShouldBeFalse("moving one component genuinely changes the geometry");
        connection.BendRadiusOverrides.ShouldBeEmpty();
        connection.IsPathValid.ShouldBeTrue();
        connection.FrozenPathStillMatchesPins().ShouldBeTrue("the re-route must reach the moved pin");
    }

    [Fact]
    public void JointMove_TranslatedRouteCollidesWithThirdComponent_FallsBackToReroute()
    {
        var (manager, router, connection, components) = RouteAcrossCorner();
        ApplyRadiusOverride(connection);

        // Place a blocker where the translated route WILL land after the joint move.
        var mid = connection.GetPathSegments()[connection.GetPathSegments().Count / 2].StartPoint;
        var blocker = CreateBlocker(mid.X + JointMoveDelta, mid.Y + JointMoveDelta);
        components.Add(blocker);

        MoveBothComponents(new List<Component> { components[0], components[1] },
            JointMoveDelta, JointMoveDelta);
        router.PathfindingGrid!.RebuildFromComponents(components);
        manager.RecalculateAllTransmissions();

        connection.IsRouteFrozen.ShouldBeFalse(
            "a translated route through a third component must be re-routed, not kept");
        connection.BendRadiusOverrides.ShouldBeEmpty();
        connection.IsPathValid.ShouldBeTrue();
        PathIntersectionDetector.IntersectsRectangle(
                connection.RoutedPath!,
                blocker.PhysicalX + 0.5, blocker.PhysicalY + 0.5,
                blocker.PhysicalX + blocker.WidthMicrometers - 0.5,
                blocker.PhysicalY + blocker.HeightMicrometers - 0.5)
            .ShouldBeFalse("the fallback re-route must avoid the third component");
    }

    [Fact]
    public void JointMoveUndoRedo_RoundTrip_RestoresGeometryAndKeepsRadii()
    {
        var (manager, router, connection, components) = RouteAcrossCorner();
        double appliedRadius = ApplyRadiusOverride(connection);
        var originalPoints = SegmentPointsOf(connection);

        // Drag both components (the move command), then undo (move back), then redo.
        foreach (var (dx, dy) in new[]
                 { (JointMoveDelta, JointMoveDelta), (-JointMoveDelta, -JointMoveDelta),
                   (JointMoveDelta, JointMoveDelta) })
        {
            MoveBothComponents(components, dx, dy);
            router.PathfindingGrid!.RebuildFromComponents(components);
            manager.RecalculateAllTransmissions();
            connection.IsRouteFrozen.ShouldBeTrue($"move by ({dx},{dy}) must keep the route frozen");
        }

        connection.BendRadiusOverrides.ShouldContainKeyAndValue(0, appliedRadius);
        AssertTranslatedBy(connection, originalPoints, JointMoveDelta, JointMoveDelta);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static void MoveBothComponents(List<Component> components, double dx, double dy)
    {
        foreach (var component in components)
        {
            component.PhysicalX += dx;
            component.PhysicalY += dy;
        }
    }

    /// <summary>
    /// Applies the largest fitting radius override to the first bend; returns it. Routes hug
    /// the pins by default, so the pin-side straight is lengthened first via the segment-shift
    /// editor — the canonical in-canvas way to make room for a bigger radius.
    /// </summary>
    private static double ApplyRadiusOverride(WaveguideConnection connection)
    {
        MakeRoomAtStartPin(connection, RadiusCandidates[0] + 10);
        var corners = BendRadiusEditor.GetBendCorners(connection.GetPathSegments());
        corners.ShouldNotBeEmpty("the corner route must expose a resizable bend");
        foreach (var radius in RadiusCandidates)
        {
            if (BendRadiusEditor.TryApplyOverride(connection, corners[0].BendIndex, radius, out _))
            {
                connection.IsRouteFrozen.ShouldBeTrue();
                return radius;
            }
        }
        throw new InvalidOperationException("No candidate radius fits the route.");
    }

    /// <summary>
    /// Extends the straight between the start pin and the first bend by shifting the middle
    /// straight along its normal (same technique as FrozenAndStyledRouteCollisionTests).
    /// </summary>
    private static void MakeRoomAtStartPin(WaveguideConnection connection, double roomMicrometers)
    {
        var segments = connection.RoutedPath!.Segments;
        var lead = (StraightSegment)segments[0];
        var handle = SegmentShiftGeometry.GetHandles(segments)
            .First(h => h.StraightIndex == 1);
        double dot = SegmentShiftGeometry.Dot(SegmentShiftGeometry.DirectionOf(lead), handle.Normal);
        SegmentShiftEditor.TryApplyShift(connection, 1, roomMicrometers * dot, out var error)
            .ShouldBeTrue(error);
    }

    private static List<(double X, double Y)> SegmentPointsOf(WaveguideConnection connection) =>
        connection.RoutedPath!.Segments
            .SelectMany(s => new[] { s.StartPoint, s.EndPoint })
            .ToList();

    private static void AssertTranslatedBy(
        WaveguideConnection connection,
        List<(double X, double Y)> originalPoints,
        double dx, double dy)
    {
        var points = SegmentPointsOf(connection);
        points.Count.ShouldBe(originalPoints.Count, "translation must not change the segment structure");
        for (int i = 0; i < points.Count; i++)
        {
            points[i].X.ShouldBe(originalPoints[i].X + dx, CoordinateTolerance);
            points[i].Y.ShouldBe(originalPoints[i].Y + dy, CoordinateTolerance);
        }
    }

    /// <summary>
    /// Two couplers offset so the connection turns a corner, routed through the manager
    /// so the grid state matches the app (same layout as FrozenAndStyledRouteCollisionTests).
    /// </summary>
    private static (WaveguideConnectionManager Manager, WaveguideRouter Router,
        WaveguideConnection Connection, List<Component> Components) RouteAcrossCorner()
    {
        var left = TestComponentFactory.CreateFourPinCouplerWithPhysicalPins("left");
        left.PhysicalX = 60;
        left.PhysicalY = 60;
        var right = TestComponentFactory.CreateFourPinCouplerWithPhysicalPins("right");
        right.PhysicalX = 560;
        right.PhysicalY = 360;
        var components = new List<Component> { left, right };

        var router = new WaveguideRouter();
        router.InitializePathfindingGrid(-100, -100, 1100, 900, components);
        var manager = new WaveguideConnectionManager(router);
        var connection = new WaveguideConnection
        {
            StartPin = Pin(left, "east0"),
            EndPin = Pin(right, "west0"),
        };
        manager.AddExistingConnection(connection);
        manager.RecalculateAllTransmissions();
        connection.IsPathValid.ShouldBeTrue("the corner route must route in the empty layout");
        return (manager, router, connection, components);
    }

    private static Component CreateBlocker(double centerX, double centerY)
    {
        const double BlockerSize = 50.0;
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin>());
        return new Component(
            new Dictionary<int, SMatrix>(), new List<Slider>(), "blocker", "",
            parts, 0, "Blocker", DiscreteRotation.R0)
        {
            WidthMicrometers = BlockerSize,
            HeightMicrometers = BlockerSize,
            PhysicalX = centerX - BlockerSize / 2,
            PhysicalY = centerY - BlockerSize / 2,
        };
    }

    private static PhysicalPin Pin(Component component, string name) =>
        component.PhysicalPins.First(p => p.Name == name);
}
