using CAP_Core.Analysis;
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
/// Collision handling for frozen and styled routes (user bug report):
/// a component dropped onto the arc belly of a manually enlarged bend must unfreeze and
/// re-route the AUTO connection; a styled route keeps its forced shape but reports the
/// collision as a design issue; an Auto sibling that cannot avoid a styled route is marked
/// blocked instead of silently crossing it.
/// </summary>
public class FrozenAndStyledRouteCollisionTests
{
    private const double BigHandleRadius = 140.0;
    private const double BlockerSize = 50.0;
    private const double BoundsTolerance = 0.5;

    [Fact]
    public void FrozenAutoRoute_HandleEditSurvivesRecalc_WhileNothingCollides()
    {
        var (manager, router, connection, components) = RouteAcrossCorner();
        ApplyBigRadius(connection);

        router.PathfindingGrid!.RebuildFromComponents(components);
        manager.RecalculateAllTransmissions();

        connection.IsRouteFrozen.ShouldBeTrue("the manual edit must survive a collision-free recalc");
        connection.BendRadiusOverrides.ShouldNotBeEmpty();
    }

    [Fact]
    public void FrozenAutoRoute_UnfreezesAndReroutes_WhenComponentDropsOntoArcBelly()
    {
        var (manager, router, connection, components) = RouteAcrossCorner();
        double appliedRadius = ApplyBigRadius(connection);
        var belly = ArcBellyOf(connection);

        var blocker = CreateBlocker(belly.X, belly.Y);
        components.Add(blocker);
        router.PathfindingGrid!.RebuildFromComponents(components);
        manager.RecalculateAllTransmissions();

        connection.IsRouteFrozen.ShouldBeFalse(
            $"a component on the r={appliedRadius} arc belly must unfreeze the route");
        connection.BendRadiusOverrides.ShouldBeEmpty("the manual edit is discarded like on an endpoint move");
        connection.IsPathValid.ShouldBeTrue();
        PathIntersectionDetector.IntersectsRectangle(
                connection.RoutedPath!,
                blocker.PhysicalX + BoundsTolerance, blocker.PhysicalY + BoundsTolerance,
                blocker.PhysicalX + blocker.WidthMicrometers - BoundsTolerance,
                blocker.PhysicalY + blocker.HeightMicrometers - BoundsTolerance)
            .ShouldBeFalse("the re-routed path must avoid the dropped component");
    }

    [Fact]
    public void StyledRoute_ComponentOnCurve_KeepsShapeAndRaisesDesignIssue()
    {
        var (manager, router, connection, components) = RouteAcrossCorner(WaveguideType.Cobra);
        connection.RoutedPath!.PassesThroughComponent.ShouldBeFalse("nothing collides yet");

        var mid = PathMidpointOf(connection);
        var blocker = CreateBlocker(mid.X, mid.Y);
        components.Add(blocker);
        router.PathfindingGrid!.RebuildFromComponents(components);
        manager.RecalculateAllTransmissions();

        connection.Type.ShouldBe(WaveguideType.Cobra, "a styled route is never auto-rerouted");
        connection.RoutedPath!.PassesThroughComponent.ShouldBeTrue(
            "the collision with the dropped component must be flagged");

        var issues = new DesignValidator().Validate(new[] { connection });
        issues.ShouldContain(i => i.Type == DesignIssueType.StyledRouteThroughComponent);
    }

    [Fact]
    public void StyledRoute_CollisionFlagClears_WhenTheComponentMovesAway()
    {
        var (manager, router, connection, components) = RouteAcrossCorner(WaveguideType.Cobra);
        var mid = PathMidpointOf(connection);
        var blocker = CreateBlocker(mid.X, mid.Y);
        components.Add(blocker);
        router.PathfindingGrid!.RebuildFromComponents(components);
        manager.RecalculateAllTransmissions();
        connection.RoutedPath!.PassesThroughComponent.ShouldBeTrue();

        components.Remove(blocker);
        router.PathfindingGrid!.RebuildFromComponents(components);
        manager.RecalculateAllTransmissions();

        connection.RoutedPath!.PassesThroughComponent.ShouldBeFalse(
            "the flag must clear once the collision is gone");
    }

    [Fact]
    public void AutoSibling_TrappedByStyledRoute_IsNeverASilentCrossing()
    {
        // Flat couplers with a 30 µm floor: the outer Cobra's slim bulge fences in the
        // inner pins, and the wide Auto U cannot stay inside it (the user's scenario).
        var top = TestComponentFactory.CreateFlatCouplerWithPhysicalPins("top");
        top.PhysicalX = 60;
        top.PhysicalY = 60;
        var bottom = TestComponentFactory.CreateFlatCouplerWithPhysicalPins("bottom");
        bottom.PhysicalX = 60;
        bottom.PhysicalY = top.PhysicalY + top.HeightMicrometers + 80;

        var router = new WaveguideRouter { ProcessMinBendRadiusMicrometers = 30.0 };
        router.InitializePathfindingGrid(-100, -100, 1100, 900, new[] { top, bottom });
        var manager = new WaveguideConnectionManager(router);
        var inner = new WaveguideConnection { StartPin = Pin(top, "east1"), EndPin = Pin(bottom, "east0") };
        var outer = new WaveguideConnection { StartPin = Pin(top, "east0"), EndPin = Pin(bottom, "east1") };
        manager.AddExistingConnection(inner);
        manager.AddExistingConnection(outer);
        manager.RecalculateAllTransmissions();

        outer.Type = WaveguideType.Cobra;
        outer.InvalidateRoute();
        manager.RecalculateAllTransmissions();

        bool crosses = PathIntersectionDetector.Crosses(inner.RoutedPath!, outer.RoutedPath!);
        (crosses && !inner.IsBlockedFallback).ShouldBeFalse(
            "an Auto route crossing its styled sibling must be marked blocked, never silent");
        outer.Type.ShouldBe(WaveguideType.Cobra);
        outer.IsBlockedFallback.ShouldBeFalse("the forced styled route itself is not degraded");
    }

    /// <summary>
    /// Two 250 µm couplers offset so the connection turns a corner; the route is calculated
    /// through the manager so it registers in the grid like in the app.
    /// </summary>
    private static (WaveguideConnectionManager Manager, WaveguideRouter Router,
        WaveguideConnection Connection, List<Component> Components)
        RouteAcrossCorner(WaveguideType type = WaveguideType.Auto)
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
            Type = type,
        };
        manager.AddExistingConnection(connection);
        manager.RecalculateAllTransmissions();
        connection.IsPathValid.ShouldBeTrue("the corner route must route in the empty layout");
        return (manager, router, connection, components);
    }

    /// <summary>
    /// Applies the biggest fitting handle radius to the first resizable bend. Routes now hug
    /// the pins by default (pin-lead-stub fix: the first bend begins one tangent from the
    /// pin), so the pin-side straight is first LENGTHENED via the segment-shift editor —
    /// the canonical way to make room for a big radius — before the override is applied.
    /// </summary>
    private static double ApplyBigRadius(WaveguideConnection connection)
    {
        MakeRoomAtStartPin(connection, BigHandleRadius + 10);
        var corners = BendRadiusEditor.GetBendCorners(connection.GetPathSegments());
        corners.ShouldNotBeEmpty("the auto corner route must expose a resizable bend");
        foreach (var radius in new[] { BigHandleRadius, 100.0, 60.0, 40.0 })
        {
            if (BendRadiusEditor.TryApplyOverride(connection, corners[0].BendIndex, radius, out _))
                return radius;
        }
        throw new InvalidOperationException("No handle radius fits the route.");
    }

    /// <summary>
    /// Extends the straight between the start pin and the first bend by
    /// <paramref name="roomMicrometers"/> by shifting the middle straight along its normal
    /// (the adjoining bends slide along the outer straights, exactly like the in-canvas drag).
    /// </summary>
    private static void MakeRoomAtStartPin(WaveguideConnection connection, double roomMicrometers)
    {
        var segments = connection.RoutedPath!.Segments;
        var lead = (StraightSegment)segments[0];
        var handle = SegmentShiftGeometry.GetHandles(segments)
            .First(h => h.StraightIndex == 1);
        // An offset o extends the lead by o / dot(leadDirection, normal); choosing
        // o = room · dot yields an extension of exactly `room` for either sign of dot.
        double dot = SegmentShiftGeometry.Dot(SegmentShiftGeometry.DirectionOf(lead), handle.Normal);
        SegmentShiftEditor.TryApplyShift(connection, 1, roomMicrometers * dot, out var error)
            .ShouldBeTrue(error);
    }

    /// <summary>Midpoint of the first (edited) arc along the path.</summary>
    private static (double X, double Y) ArcBellyOf(WaveguideConnection connection)
    {
        var bend = connection.GetPathSegments().OfType<BendSegment>().First();
        return ArcSampling.SamplePoints(bend, bend.LengthMicrometers / 2).Skip(1).First();
    }

    /// <summary>Point halfway along the path's segment list (blocker drop position).</summary>
    private static (double X, double Y) PathMidpointOf(WaveguideConnection connection)
    {
        var segments = connection.GetPathSegments();
        return segments[segments.Count / 2].StartPoint;
    }

    /// <summary>A pinless square component centered on the given point.</summary>
    private static Component CreateBlocker(double centerX, double centerY)
    {
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin>());
        return new Component(
            new Dictionary<int, SMatrix>(), new List<CAP_Core.Components.Core.Slider>(), "blocker", "",
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
