using CAP_Core.Components;
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
/// Hardening contract of the pin-lead collapse against every consumer that judges collapsed
/// geometry: a foreign component dropped onto a collapsed route still invalidates it, a bend
/// hugging its OWN pin never unfreezes a manual edit or raises a false collision flag, and
/// the sibling rules veto only real risks — a far-away degenerate neighbour or an already
/// crossing blocked fallback must not suppress the collapse.
/// </summary>
public class PinLeadCollapseHardeningTests
{
    private const double Radius = 10.0;

    /// <summary>Residual tolerance for a "collapsed to the pin" lead (floating-point noise).</summary>
    private const double CollapsedTolerance = 1e-3;

    /// <summary>
    /// A foreign, unconnected component whose body overlaps the collapsed departure bend must
    /// invalidate the route on the next pass (pre-collapse-feature behavior). The body sits in
    /// the zone the routing-time pin corridor opens, where an ownership-blind corridor clearing
    /// during validation would wrongly accept it — foreign bodies block everywhere.
    /// </summary>
    [Fact]
    public void ForeignComponentOnCollapsedRoute_InvalidatesTheRoute_AndReroutes()
    {
        var (manager, router, connection, startPin, _) = RouteUTurn();
        var collapsed = connection.RoutedPath!;
        StartPinLead(collapsed, startPin).ShouldBe(0, CollapsedTolerance,
            "precondition: the departure lead is collapsed onto the pin");

        var foreign = CreateTestComponent(53, 23, width: 17, height: 3.9);
        router.AddComponentObstacle(foreign);

        manager.RecalculateAllTransmissions();

        connection.RoutedPath.ShouldNotBeSameAs(collapsed,
            "a foreign component body on the collapsed route must invalidate it");
        connection.IsPathValid.ShouldBeTrue();
    }

    /// <summary>
    /// A segment shift freezes the route; the collapsed pin hug (bend beginning on the pin,
    /// inside the own padding band) must NOT read as a component collision on the next
    /// recalculation — unfreezing here would silently wipe the user's manual edit.
    /// </summary>
    [Fact]
    public void SegmentShift_OnCollapsedRoute_StaysFrozenAcrossRecalculate()
    {
        var (manager, _, connection, _, _) = RouteUTurn();
        ShiftFirstShiftableStraight(connection);
        connection.IsRouteFrozen.ShouldBeTrue("precondition: the manual shift freezes the route");
        var frozenPath = connection.RoutedPath!;
        var offsets = new Dictionary<int, double>(connection.StraightShiftOffsets);

        manager.RecalculateAllTransmissions();

        connection.IsRouteFrozen.ShouldBeTrue(
            "a bend hugging its own pin is no collision — unfreezing would destroy the manual edit");
        connection.RoutedPath.ShouldBeSameAs(frozenPath);
        connection.StraightShiftOffsets.Count.ShouldBe(offsets.Count);
        foreach (var (index, offset) in offsets)
            connection.StraightShiftOffsets[index].ShouldBe(offset, 1e-9);
        connection.BendRadiusOverrides.ShouldBeEmpty();
    }

    /// <summary>
    /// A shift drag on a collapsed route must not flag <c>PassesThroughComponent</c>: the pin
    /// hug lives in the own padding band, which the shared collision predicate tolerates.
    /// </summary>
    [Fact]
    public void SegmentShift_OnCollapsedRoute_DoesNotFlagComponentCollision()
    {
        var (_, router, connection, _, _) = RouteUTurn();
        ShiftFirstShiftableStraight(connection);

        SegmentShiftEditor.RefreshComponentCollision(connection, router);

        connection.RoutedPath!.PassesThroughComponent.ShouldBeFalse(
            "a collapsed pin hug inside the own padding band is not a component collision");
    }

    /// <summary>
    /// A zero-length sibling 50 µm away is unmeasurable as a polyline, but its REAL distance
    /// is far above the waveguide spacing — it must not veto the collapse.
    /// </summary>
    [Fact]
    public void DegenerateSiblingWithinReach_ButFarAway_DoesNotVetoTheCollapse()
    {
        var anchor = CreateTestComponent(300, 140);
        var (manager, _, connection, startPin, endPin) = SetUpManualUTurn(anchor);

        var degenerate = new WaveguideConnection
        {
            StartPin = Pin(anchor, -170, 10, 0),
            EndPin = Pin(anchor, -170, 10, 180),
            IsRouteFrozen = true,
        };
        var point = new RoutedPath();
        point.Segments.Add(new StraightSegment(130, 150, 130, 150, 0));
        degenerate.RestoreCachedPath(point);
        manager.AddExistingConnection(degenerate);

        manager.RecalculateAllTransmissions();

        StartPinLead(connection.RoutedPath!, startPin).ShouldBe(0, CollapsedTolerance,
            "a degenerate sibling far above the spacing must not block the collapse");
        EndPinLead(connection.RoutedPath!, endPin).ShouldBe(0, CollapsedTolerance);
    }

    /// <summary>
    /// A blocked fallback crosses other routes by construction. The existing crossing is no
    /// veto: the collapse is accepted as long as the number of crossings with that sibling
    /// does not increase and no collinear overlap arises.
    /// </summary>
    [Fact]
    public void BlockedFallbackSiblingAlreadyCrossing_DoesNotVetoTheCollapse()
    {
        var anchor = CreateTestComponent(400, 0);
        var (manager, _, connection, startPin, endPin) = SetUpManualUTurn(anchor);

        var fallback = new WaveguideConnection
        {
            StartPin = Pin(anchor, -380, 150, 0),
            EndPin = Pin(anchor, -250, 150, 180),
            IsRouteFrozen = true,
        };
        var line = new RoutedPath { IsBlockedFallback = true };
        line.Segments.Add(new StraightSegment(20, 150, 150, 150, 0));
        fallback.RestoreCachedPath(line);
        manager.AddExistingConnection(fallback);

        manager.RecalculateAllTransmissions();

        var path = connection.RoutedPath!;
        StartPinLead(path, startPin).ShouldBe(0, CollapsedTolerance,
            "an existing fallback crossing must not veto the collapse");
        EndPinLead(path, endPin).ShouldBe(0, CollapsedTolerance);
        PathIntersectionDetector.CrossingCount(path, fallback.RoutedPath!).ShouldBe(1,
            "the collapse must not add crossings with the fallback sibling");
    }

    /// <summary>
    /// The own-pin allowance must be sized with the radius the route was ACTUALLY built with:
    /// a route that violates the process floor was built at the raw connection radius, not at
    /// the floor — sizing the allowance from the floor would tolerate cells its arcs never reach.
    /// </summary>
    [Fact]
    public void RoutedBendRadius_UsesConnectionRadius_WhenRouteViolatesProcessFloor()
    {
        var connection = new WaveguideConnection { BendRadiusMicrometers = 5 };
        var path = new RoutedPath { ViolatesProcessMinBendRadius = true };
        path.Segments.Add(new StraightSegment(0, 0, 10, 0, 0));
        connection.RestoreCachedPath(path);

        connection.RoutedBendRadiusMicrometers(processMinBendRadiusMicrometers: 25).ShouldBe(5);

        connection.RoutedPath!.ViolatesProcessMinBendRadius = false;
        connection.RoutedBendRadiusMicrometers(processMinBendRadiusMicrometers: 25).ShouldBe(25);
    }

    /// <summary>Shifts the first shiftable straight, trying both normal directions so the
    /// test is robust against the router's orientation of the middle straight.</summary>
    private static void ShiftFirstShiftableStraight(WaveguideConnection connection)
    {
        var handles = SegmentShiftGeometry.GetHandles(connection.GetPathSegments());
        handles.ShouldNotBeEmpty("the collapsed route must expose a shiftable straight");
        var handle = handles[0];

        if (!SegmentShiftEditor.TryApplyShift(connection, handle.StraightIndex, 4.0, out _) &&
            !SegmentShiftEditor.TryApplyShift(connection, handle.StraightIndex, -4.0, out var error))
        {
            throw new InvalidOperationException($"Neither shift direction fits the route: {error}");
        }
    }

    /// <summary>Routes the symmetric U-turn through the real pipeline (A* + collapse pass).</summary>
    private static (WaveguideConnectionManager Manager, WaveguideRouter Router,
        WaveguideConnection Connection, PhysicalPin StartPin, PhysicalPin EndPin) RouteUTurn()
    {
        var (router, startPin, endPin) = CreateUTurnLayout(gridSize: 500);
        var manager = new WaveguideConnectionManager(router);
        var connection = new WaveguideConnection { StartPin = startPin, EndPin = endPin };
        manager.AddExistingConnection(connection);
        manager.RecalculateAllTransmissions();
        connection.IsPathValid.ShouldBeTrue();
        return (manager, router, connection, startPin, endPin);
    }

    /// <summary>
    /// The same U-turn layout, but with a pre-built cached path (20 µm pin leads, middle
    /// straight at x=80) instead of running A*, so the pre-collapse geometry is exact and the
    /// sibling constraints are evaluated against known distances.
    /// </summary>
    private static (WaveguideConnectionManager Manager, WaveguideRouter Router,
        WaveguideConnection Connection, PhysicalPin StartPin, PhysicalPin EndPin)
        SetUpManualUTurn(params Component[] extraComponents)
    {
        var (router, startPin, endPin) = CreateUTurnLayout(gridSize: 700, extraComponents);
        var manager = new WaveguideConnectionManager(router);
        var connection = new WaveguideConnection { StartPin = startPin, EndPin = endPin };
        connection.RestoreCachedPath(ManualUTurnPath());
        manager.AddExistingConnection(connection);
        return (manager, router, connection, startPin, endPin);
    }

    private static (WaveguideRouter Router, PhysicalPin StartPin, PhysicalPin EndPin)
        CreateUTurnLayout(double gridSize, params Component[] extraComponents)
    {
        var router = new WaveguideRouter
        {
            MinBendRadiusMicrometers = Radius,
            MinWaveguideSpacingMicrometers = 2.0,
            UseDiagonalRouting = false,
        };
        var start = CreateTestComponent(0, 0);
        var end = CreateTestComponent(0, 275);
        var components = new List<Component> { start, end };
        components.AddRange(extraComponents);
        router.InitializePathfindingGrid(-100, -100, gridSize, gridSize, components);
        return (router, Pin(start, 50, 25, 0), Pin(end, 50, 25, 0));
    }

    /// <summary>U-turn with 20 µm pin leads: east lead, up, north straight at x=80, up to
    /// heading west, west arrival lead — both leads are pure detour, one shift zeroes both.</summary>
    private static RoutedPath ManualUTurnPath()
    {
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(50, 25, 70, 25, 0));
        path.Segments.Add(new BendSegment(70, 35, Radius, 0, 90));
        path.Segments.Add(new StraightSegment(80, 35, 80, 290, 90));
        path.Segments.Add(new BendSegment(70, 290, Radius, 90, 90));
        path.Segments.Add(new StraightSegment(70, 300, 50, 300, 180));
        return path;
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

    private static PhysicalPin Pin(Component parent, double offsetX, double offsetY, double angle) => new()
    {
        Name = $"pin_{offsetX}_{offsetY}_{angle}",
        OffsetXMicrometers = offsetX,
        OffsetYMicrometers = offsetY,
        AngleDegrees = angle,
        ParentComponent = parent,
    };

    private static Component CreateTestComponent(
        double x, double y, double width = 50, double height = 50)
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
            WidthMicrometers = width,
            HeightMicrometers = height,
            PhysicalX = x,
            PhysicalY = y,
        };
    }
}
