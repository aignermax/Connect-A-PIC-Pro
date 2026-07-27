using CAP_Core.Components;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Routing;

/// <summary>
/// The A* escape and grid quantization leave a short forced straight between a pin and the
/// first/last bend. The manager's collapse pass (<see cref="PinStraightCollapser"/> via
/// <c>CollapseAutoRoutePinLeads</c>) pulls the bend onto the pin whenever it stays clear of
/// siblings and component bodies. These tests pin that contract on real routed connections:
/// the lead collapses to zero where geometry allows, deterministically, without introducing a
/// collision or self-intersection.
/// </summary>
public class PinStraightAutoCollapseTests
{
    private const double Radius = 10.0;

    /// <summary>Residual tolerance for a "collapsed to the pin" lead (floating-point noise).</summary>
    private const double CollapsedTolerance = 1e-3;

    /// <summary>
    /// U-turn layout (both pins face east): the route leaves east, turns north, and returns west.
    /// The east departure and the west arrival straights are pure detour — the collapse pass must
    /// drive BOTH to zero, so the first and last bends begin/end exactly at the pins.
    /// </summary>
    [Fact]
    public void AutoRoute_CollapsesBothPinLeads_OnSymmetricUTurn()
    {
        var (_, connection, startPin, endPin) = RouteUTurn(300);

        var path = connection.RoutedPath!;
        connection.IsPathValid.ShouldBeTrue();
        StartPinLead(path, startPin).ShouldBe(0, CollapsedTolerance,
            "the departure bend must sit directly at the start pin after the collapse pass");
        EndPinLead(path, endPin).ShouldBe(0, CollapsedTolerance,
            "the arrival bend must sit directly at the end pin after the collapse pass");
    }

    /// <summary>Determinism: the same layout must yield the same collapsed geometry every time.</summary>
    [Fact]
    public void AutoRoute_CollapseIsDeterministic()
    {
        var (_, connectionA, _, _) = RouteUTurn(300);
        var (_, connectionB, _, _) = RouteUTurn(300);

        var a = connectionA.RoutedPath!;
        var b = connectionB.RoutedPath!;
        a.Segments.Count.ShouldBe(b.Segments.Count);
        for (int i = 0; i < a.Segments.Count; i++)
        {
            a.Segments[i].StartPoint.X.ShouldBe(b.Segments[i].StartPoint.X, 1e-9);
            a.Segments[i].StartPoint.Y.ShouldBe(b.Segments[i].StartPoint.Y, 1e-9);
            a.Segments[i].EndPoint.X.ShouldBe(b.Segments[i].EndPoint.X, 1e-9);
            a.Segments[i].EndPoint.Y.ShouldBe(b.Segments[i].EndPoint.Y, 1e-9);
        }
    }

    /// <summary>
    /// Tangency is preserved: collapsing the lead only translates the first bend onto the pin, it
    /// does not rotate it — the arc still leaves the pin along the pin heading (0°).
    /// </summary>
    [Fact]
    public void AutoRoute_CollapsedFirstBend_StaysTangentialToThePin()
    {
        var (_, connection, startPin, _) = RouteUTurn(300);

        var firstBend = connection.RoutedPath!.Segments.OfType<BendSegment>().First();
        NormalizeAngle(firstBend.StartAngleDegrees).ShouldBe(0, 1e-6);
        var (sx, sy) = startPin.GetAbsolutePosition();
        firstBend.StartPoint.X.ShouldBe(sx, CollapsedTolerance);
        firstBend.StartPoint.Y.ShouldBe(sy, CollapsedTolerance);
    }

    /// <summary>
    /// The collapse only translates existing geometry, so the path stays connected and free of
    /// self-intersection (the collapsed pin-lead straight is kept at zero length, mirroring the
    /// manual segment-shift, so the first/last bend begins exactly on the pin).
    /// </summary>
    [Fact]
    public void AutoRoute_CollapsedPath_StaysConnectedAndSimple()
    {
        var (_, connection, _, _) = RouteUTurn(300);

        var path = connection.RoutedPath!;
        path.IsValid.ShouldBeTrue();
        PathIntersectionDetector.HasSelfIntersection(path).ShouldBeFalse();
    }

    /// <summary>
    /// [0] The collapse must publish a fresh path instance, never mutate the live one the UI
    /// thread may be enumerating. After a re-route the previously published path is left byte-for-
    /// byte intact and the connection points at a different instance.
    /// </summary>
    [Fact]
    public void AutoRoute_CollapsePublishesNewInstance_WithoutMutatingThePrevious()
    {
        var (manager, connection, _, _) = RouteUTurn(300);
        var published = connection.RoutedPath!;
        var snapshot = published.DeepCopy();

        connection.InvalidateRoute();
        manager.RecalculateAllTransmissions();

        connection.RoutedPath.ShouldNotBeSameAs(published, "the collapse must swap in a new instance");
        published.Segments.Count.ShouldBe(snapshot.Segments.Count);
        for (int i = 0; i < snapshot.Segments.Count; i++)
        {
            published.Segments[i].StartPoint.ShouldBe(snapshot.Segments[i].StartPoint);
            published.Segments[i].EndPoint.ShouldBe(snapshot.Segments[i].EndPoint);
        }
    }

    /// <summary>
    /// [4] A collapsed route must satisfy the exact check the incremental router applies next
    /// pass, so it is KEPT rather than re-routed forever. A second recalculation with nothing
    /// changed leaves the very same path instance in place.
    /// </summary>
    [Fact]
    public void AutoRoute_CollapsedRoute_SurvivesTheNextRecalc_Unchanged()
    {
        var (manager, connection, _, _) = RouteUTurn(300);
        var afterFirst = connection.RoutedPath!;

        manager.RecalculateAllTransmissions();

        connection.RoutedPath.ShouldBeSameAs(afterFirst,
            "the collapsed route must be kept by incremental routing, not re-routed and re-collapsed");
    }

    /// <summary>
    /// [1] The collapse acceptance must see EVERY component (grid obstacle), not just the two
    /// endpoint bodies: a route may hug its own endpoint component's padded pin cells, but a path
    /// through an unconnected component is a real collision.
    /// </summary>
    [Fact]
    public void ForeignComponentCheck_ExemptsOwnPins_ButFlagsUnconnectedComponents()
    {
        var router = new WaveguideRouter { MinBendRadiusMicrometers = Radius };
        var start = CreateTestComponent(0, 0);
        var end = CreateTestComponent(0, 275);
        var blocker = CreateTestComponent(200, 130);
        router.InitializePathfindingGrid(-100, -100, 500, 500, new[] { start, end, blocker });

        var startPin = Pin(start, 50, 25, 0);
        var endPin = Pin(end, 50, 25, 0);

        // A segment running through the start component's own padded cells (x in [50,55]) is the
        // pin-hug the collapse produces — it must be exempt.
        var hugsOwnPin = new RoutedPath();
        hugsOwnPin.Segments.Add(new StraightSegment(52, 5, 52, 45, 90));
        router.IsPathBlockedByForeignComponents(hugsOwnPin.Segments, startPin, endPin)
            .ShouldBeFalse("a route may touch its own endpoint component near the pin");

        // A segment cutting through the unconnected blocker body is a real collision.
        var throughBlocker = new RoutedPath();
        throughBlocker.Segments.Add(new StraightSegment(200, 130, 250, 180, 45));
        router.IsPathBlockedByForeignComponents(throughBlocker.Segments, startPin, endPin)
            .ShouldBeTrue("a route through an unconnected component must be flagged");
    }

    /// <summary>
    /// [2] The collapse must never bring two routes into a crossing. Two nested U-turns are routed
    /// together; after the collapse pass no pair of routes may touch or cross.
    /// </summary>
    [Fact]
    public void AutoRoute_CollapseNeverIntroducesASiblingCrossing()
    {
        var router = new WaveguideRouter
        {
            MinBendRadiusMicrometers = Radius,
            MinWaveguideSpacingMicrometers = 2.0,
            UseDiagonalRouting = false,
        };
        var start = CreateTestComponent(0, 0);
        var end = CreateTestComponent(0, 275);
        router.InitializePathfindingGrid(-200, -200, 700, 700, new[] { start, end });

        var manager = new WaveguideConnectionManager(router);
        var inner = new WaveguideConnection { StartPin = Pin(start, 50, 20, 0), EndPin = Pin(end, 50, 20, 0) };
        var outer = new WaveguideConnection { StartPin = Pin(start, 50, 30, 0), EndPin = Pin(end, 50, 30, 0) };
        manager.AddExistingConnection(inner);
        manager.AddExistingConnection(outer);
        manager.RecalculateAllTransmissions();

        inner.IsPathValid.ShouldBeTrue();
        outer.IsPathValid.ShouldBeTrue();
        PathIntersectionDetector.MinimumDistance(inner.RoutedPath!, outer.RoutedPath!)
            .ShouldBeGreaterThan(0.0, "the collapse must not pull the two routes into a crossing");
    }

    private static double StartPinLead(RoutedPath path, PhysicalPin startPin)
    {
        var (sx, sy) = startPin.GetAbsolutePosition();
        var firstBend = path.Segments.OfType<BendSegment>().First();
        return Distance(sx, sy, firstBend.StartPoint.X, firstBend.StartPoint.Y);
    }

    private static double EndPinLead(RoutedPath path, PhysicalPin endPin)
    {
        var (ex, ey) = endPin.GetAbsolutePosition();
        var lastBend = path.Segments.OfType<BendSegment>().Last();
        return Distance(ex, ey, lastBend.EndPoint.X, lastBend.EndPoint.Y);
    }

    private static double Distance(double x1, double y1, double x2, double y2)
        => Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));

    private static double NormalizeAngle(double degrees)
    {
        double a = degrees % 360.0;
        if (a < 0) a += 360.0;
        return a;
    }

    private static (WaveguideConnectionManager Manager, WaveguideConnection Connection,
        PhysicalPin StartPin, PhysicalPin EndPin) RouteUTurn(double pinSeparationY)
    {
        var router = new WaveguideRouter
        {
            MinBendRadiusMicrometers = Radius,
            MinWaveguideSpacingMicrometers = 2.0,
            UseDiagonalRouting = false,
        };
        var start = CreateTestComponent(0, 0);
        var end = CreateTestComponent(0, pinSeparationY - 25);
        router.InitializePathfindingGrid(-100, -100, 500, 500, new[] { start, end });

        var startPin = Pin(start, 50, 25, 0);
        var endPin = Pin(end, 50, 25, 0);

        var manager = new WaveguideConnectionManager(router);
        var connection = new WaveguideConnection { StartPin = startPin, EndPin = endPin };
        manager.AddExistingConnection(connection);
        manager.RecalculateAllTransmissions();
        return (manager, connection, startPin, endPin);
    }

    private static PhysicalPin Pin(Component parent, double offsetX, double offsetY, double angle) => new()
    {
        Name = $"pin_{offsetX}_{offsetY}",
        OffsetXMicrometers = offsetX,
        OffsetYMicrometers = offsetY,
        AngleDegrees = angle,
        ParentComponent = parent,
    };

    private static Component CreateTestComponent(double x, double y)
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
            identifier: $"TestComponent_{x}_{y}",
            rotationCounterClock: DiscreteRotation.R0)
        {
            WidthMicrometers = 50,
            HeightMicrometers = 50,
            PhysicalX = x,
            PhysicalY = y,
        };
    }
}
