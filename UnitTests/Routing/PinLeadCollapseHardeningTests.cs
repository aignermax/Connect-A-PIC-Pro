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
/// Hardening contract of the pin-lead collapse. The acceptance is the UNCHANGED component check
/// (no special validation semantics), so: a foreign component dropped onto a collapsed route
/// still invalidates it, a collapsed pin hug — which lives entirely inside the persistent pin
/// corridor — never unfreezes a manual edit or raises a false collision flag, a small radius
/// whose arc would cut the padding corner collapses only partially, and the sibling rules veto
/// conservatively: an existing touch/crossing prevents the collapse, while a far-away degenerate
/// neighbour does not.
/// </summary>
public class PinLeadCollapseHardeningTests
{
    /// <summary>Bend radius (µm) at which A* leaves quantization leads that the collapse pass
    /// removes; the collapsed arc traverses the own padding band inside the persistent pin
    /// corridor, so the full collapse passes the unchanged component check and leaves a
    /// zero-length lead plus a shiftable middle straight.</summary>
    private const double Radius = 10.0;

    /// <summary>Residual tolerance for a "collapsed to the pin" lead (floating-point noise).</summary>
    private const double CollapsedTolerance = 1e-3;

    /// <summary>Pin-lead length (µm) of the manually built U-turn fixtures.</summary>
    private const double ManualLeadMicrometers = 20.0;

    /// <summary>
    /// A foreign, unconnected component whose body overlaps the collapsed departure bend must
    /// invalidate the route on the next pass — the collapse introduces no exemption anywhere,
    /// so the pre-collapse-feature behavior applies unchanged.
    /// </summary>
    [Fact]
    public void ForeignComponentOnCollapsedRoute_InvalidatesTheRoute_AndReroutes()
    {
        var (manager, router, connection, startPin, _) = RouteUTurn();
        var collapsed = connection.RoutedPath!;
        StartPinLead(collapsed, startPin).ShouldBe(0, CollapsedTolerance,
            "precondition: the departure lead is collapsed onto the pin");

        var foreign = CreateTestComponent(66, 26, width: 10, height: 10);
        router.AddComponentObstacle(foreign);

        manager.RecalculateAllTransmissions();

        connection.RoutedPath.ShouldNotBeSameAs(collapsed,
            "a foreign component body on the collapsed route must invalidate it");
        connection.IsPathValid.ShouldBeTrue();
    }

    /// <summary>
    /// A segment shift freezes the route; the collapsed pin hug (bend beginning on the pin,
    /// inside the persistent pin corridor) must NOT read as a component collision on the next
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
            "a bend hugging its own pin inside the pin corridor is no collision — unfreezing would destroy the manual edit");
        connection.RoutedPath.ShouldBeSameAs(frozenPath);
        connection.StraightShiftOffsets.Count.ShouldBe(offsets.Count);
        foreach (var (index, offset) in offsets)
            connection.StraightShiftOffsets[index].ShouldBe(offset, 1e-9);
        connection.BendRadiusOverrides.ShouldBeEmpty();
    }

    /// <summary>
    /// A shift drag on a collapsed route must not flag <c>PassesThroughComponent</c>: the pin
    /// hug lives inside the persistent pin corridor, which the unchanged component check sees
    /// as free cells.
    /// </summary>
    [Fact]
    public void SegmentShift_OnCollapsedRoute_DoesNotFlagComponentCollision()
    {
        var (_, router, connection, _, _) = RouteUTurn();
        ShiftFirstShiftableStraight(connection);

        SegmentShiftEditor.RefreshComponentCollision(connection, router);

        connection.RoutedPath!.PassesThroughComponent.ShouldBeFalse(
            "a collapsed pin hug inside the pin corridor is not a component collision");
    }

    /// <summary>
    /// A small bend radius rises too fast and would cut the padding corner beyond the persistent
    /// pin corridor. The collapse must not force it through: the lead collapses PARTIALLY to the
    /// largest shift the unchanged component check accepts, and the result is stable across the
    /// next recalculation (no per-pass micro-shaving).
    /// </summary>
    [Fact]
    public void SmallRadius_CollapsesPartially_AndStaysStableAcrossRecalculate()
    {
        var (manager, router, connection, startPin, endPin) = SetUpManualUTurn(bendRadius: 5.0);

        manager.RecalculateAllTransmissions();

        var path = connection.RoutedPath!;
        double startLead = StartPinLead(path, startPin);
        double endLead = EndPinLead(path, endPin);
        startLead.ShouldBeGreaterThan(CollapsedTolerance,
            "the tight arc would cut the padding corner — a residual lead is the correct result");
        startLead.ShouldBeLessThan(ManualLeadMicrometers - 1.0,
            "the lead must still collapse up to the accepted boundary");
        endLead.ShouldBe(startLead, 0.1, "one shared shift drives both leads of the U-turn");
        router.IsPathBlockedByComponents(path.Segments).ShouldBeFalse(
            "the partial collapse must stop before touching any component cell");

        manager.RecalculateAllTransmissions();
        connection.RoutedPath.ShouldBeSameAs(path,
            "the partial collapse must converge — follow-up passes shave nothing further");
    }

    /// <summary>
    /// A zero-length sibling 50 µm away is unmeasurable as a polyline, but its REAL distance
    /// is far above the waveguide spacing — it must not veto the collapse.
    /// </summary>
    [Fact]
    public void DegenerateSiblingWithinReach_ButFarAway_DoesNotVetoTheCollapse()
    {
        var anchor = CreateTestComponent(300, 140);
        var (manager, _, connection, startPin, endPin) = SetUpManualUTurn(10.0, anchor);

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
    /// A route that already touches or crosses a sibling — a blocked fallback does by
    /// construction — is conservatively NEVER collapsed: the collapse must not risk adding or
    /// worsening a crossing. The manual segment-shift handle remains the escape hatch.
    /// </summary>
    [Fact]
    public void SiblingAlreadyCrossing_ConservativelyPreventsTheCollapse()
    {
        var anchor = CreateTestComponent(400, 0);
        var (manager, _, connection, startPin, endPin) = SetUpManualUTurn(10.0, anchor);
        var original = connection.RoutedPath!;

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

        connection.RoutedPath.ShouldBeSameAs(original,
            "an already-crossing route is left untouched by the collapse pass");
        StartPinLead(original, startPin).ShouldBe(ManualLeadMicrometers, CollapsedTolerance);
        EndPinLead(original, endPin).ShouldBe(ManualLeadMicrometers, CollapsedTolerance);
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
        var (router, startPin, endPin) = CreateUTurnLayout(Radius, gridSize: 500);
        var manager = new WaveguideConnectionManager(router);
        var connection = new WaveguideConnection
        {
            StartPin = startPin,
            EndPin = endPin,
            BendRadiusMicrometers = Radius,
        };
        manager.AddExistingConnection(connection);
        manager.RecalculateAllTransmissions();
        connection.IsPathValid.ShouldBeTrue();
        return (manager, router, connection, startPin, endPin);
    }

    /// <summary>
    /// The same U-turn layout, but with a pre-built cached path (20 µm pin leads, middle
    /// straight at x = 70 + radius) instead of running A*, so the pre-collapse geometry is
    /// exact and the collapse decisions are evaluated against known distances.
    /// </summary>
    private static (WaveguideConnectionManager Manager, WaveguideRouter Router,
        WaveguideConnection Connection, PhysicalPin StartPin, PhysicalPin EndPin)
        SetUpManualUTurn(double bendRadius, params Component[] extraComponents)
    {
        var (router, startPin, endPin) = CreateUTurnLayout(bendRadius, 700, extraComponents);
        var manager = new WaveguideConnectionManager(router);
        var connection = new WaveguideConnection
        {
            StartPin = startPin,
            EndPin = endPin,
            BendRadiusMicrometers = bendRadius,
        };
        connection.RestoreCachedPath(ManualUTurnPath(bendRadius));
        manager.AddExistingConnection(connection);
        return (manager, router, connection, startPin, endPin);
    }

    private static (WaveguideRouter Router, PhysicalPin StartPin, PhysicalPin EndPin)
        CreateUTurnLayout(double bendRadius, double gridSize, params Component[] extraComponents)
    {
        var router = new WaveguideRouter
        {
            MinBendRadiusMicrometers = bendRadius,
            MinWaveguideSpacingMicrometers = 2.0,
            UseDiagonalRouting = false,
        };
        var start = CreateTestComponent(0, 0);
        var end = CreateTestComponent(0, 275);
        var startPin = Pin(start, 50, 25, 0);
        var endPin = Pin(end, 50, 25, 0);
        // Registered pins carve the persistent pin corridors during the grid rebuild,
        // exactly like every real component — the corridor is what lets a collapsed
        // bend hug its pin without touching component padding.
        start.PhysicalPins.Add(startPin);
        end.PhysicalPins.Add(endPin);
        var components = new List<Component> { start, end };
        components.AddRange(extraComponents);
        router.InitializePathfindingGrid(-100, -100, gridSize, gridSize, components);
        return (router, startPin, endPin);
    }

    /// <summary>U-turn with 20 µm pin leads: east lead, up (radius), north straight at
    /// x = 70 + radius, up to heading west, west arrival lead — both leads are pure detour,
    /// one shift zeroes both.</summary>
    private static RoutedPath ManualUTurnPath(double radius)
    {
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(50, 25, 70, 25, 0));
        path.Segments.Add(new BendSegment(70, 25 + radius, radius, 0, 90));
        path.Segments.Add(new StraightSegment(70 + radius, 25 + radius, 70 + radius, 300 - radius, 90));
        path.Segments.Add(new BendSegment(70, 300 - radius, radius, 90, 90));
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
