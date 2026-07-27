using CAP_Core.Analysis;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Routing.InterconnectRouting.SegmentShift;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Routing;

/// <summary>
/// End-to-end behavior of the segment parallel shift on real routed connections (issue #791):
/// a shift into a component is flagged through the design-issue pipeline instead of snapping
/// back, the edit survives a collision-free recalc (frozen route), and a component dropped
/// onto the shifted path unfreezes the Auto route like any other manual edit.
/// </summary>
public class SegmentShiftCollisionTests
{
    private const double ShiftMicrometers = 40.0;
    private const double BlockerSize = 50.0;

    [Fact]
    public void ShiftIntoComponent_FlagsCollision_AndRaisesDesignIssue()
    {
        var (_, router, connection, components) = RouteAcrossCorner();
        var handle = ApplyShift(connection);

        var shifted = ShiftedMidpoint(connection, handle.StraightIndex);
        components.Add(CreateBlocker(shifted.X, shifted.Y));
        router.PathfindingGrid!.RebuildFromComponents(components);
        SegmentShiftEditor.RefreshComponentCollision(connection, router);

        connection.RoutedPath!.PassesThroughComponent.ShouldBeTrue(
            "the shift moved the segment into the blocker — the collision must be flagged");
        new DesignValidator().Validate(new[] { connection })
            .ShouldContain(i => i.Type == DesignIssueType.StyledRouteThroughComponent);
    }

    [Fact]
    public void ShiftedRoute_SurvivesRecalc_WhileNothingCollides()
    {
        var (manager, router, connection, components) = RouteAcrossCorner();
        var handle = ApplyShift(connection);
        var shiftedBefore = ShiftedMidpoint(connection, handle.StraightIndex);

        router.PathfindingGrid!.RebuildFromComponents(components);
        manager.RecalculateAllTransmissions();

        connection.IsRouteFrozen.ShouldBeTrue("the manual shift must survive a collision-free recalc");
        connection.StraightShiftOffsets.ShouldNotBeEmpty();
        var shiftedAfter = ShiftedMidpoint(connection, handle.StraightIndex);
        shiftedAfter.X.ShouldBe(shiftedBefore.X, 1e-6);
        shiftedAfter.Y.ShouldBe(shiftedBefore.Y, 1e-6);
    }

    [Fact]
    public void ShiftedAutoRoute_UnfreezesAndReroutes_WhenComponentDropsOntoIt()
    {
        var (manager, router, connection, components) = RouteAcrossCorner();
        var handle = ApplyShift(connection);

        var shifted = ShiftedMidpoint(connection, handle.StraightIndex);
        components.Add(CreateBlocker(shifted.X, shifted.Y));
        router.PathfindingGrid!.RebuildFromComponents(components);
        manager.RecalculateAllTransmissions();

        connection.IsRouteFrozen.ShouldBeFalse(
            "a component dropped onto the shifted segment must unfreeze the Auto route");
        connection.StraightShiftOffsets.ShouldBeEmpty("the manual edit is discarded like a bend override");
        connection.IsPathValid.ShouldBeTrue();
    }

    /// <summary>Shifts the first shiftable straight of the routed corner path, trying both
    /// normal directions so the test is robust against the router's corner orientation.</summary>
    private static StraightSegmentHandle ApplyShift(WaveguideConnection connection)
    {
        var handles = SegmentShiftGeometry.GetHandles(connection.GetPathSegments());
        handles.ShouldNotBeEmpty("the auto corner route must expose a shiftable straight");
        var handle = handles[0];

        if (!SegmentShiftEditor.TryApplyShift(connection, handle.StraightIndex, ShiftMicrometers, out _) &&
            !SegmentShiftEditor.TryApplyShift(connection, handle.StraightIndex, -ShiftMicrometers, out var error))
        {
            throw new InvalidOperationException($"Neither shift direction fits the route: {error}");
        }
        return handle;
    }

    /// <summary>Current midpoint of the shifted straight segment (recomputed after the edit).</summary>
    private static (double X, double Y) ShiftedMidpoint(WaveguideConnection connection, int straightIndex)
    {
        var straight = connection.GetPathSegments().OfType<StraightSegment>().ElementAt(straightIndex);
        return ((straight.StartPoint.X + straight.EndPoint.X) / 2.0,
                (straight.StartPoint.Y + straight.EndPoint.Y) / 2.0);
    }

    /// <summary>
    /// Two 250 µm couplers offset so the connection turns a corner; the route is calculated
    /// through the manager so it registers in the grid like in the app (mirrors
    /// <see cref="FrozenAndStyledRouteCollisionTests"/>).
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
            StartPin = left.PhysicalPins.First(p => p.Name == "east0"),
            EndPin = right.PhysicalPins.First(p => p.Name == "west0"),
        };
        manager.AddExistingConnection(connection);
        manager.RecalculateAllTransmissions();
        connection.IsPathValid.ShouldBeTrue("the corner route must route in the empty layout");
        return (manager, router, connection, components);
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
}
