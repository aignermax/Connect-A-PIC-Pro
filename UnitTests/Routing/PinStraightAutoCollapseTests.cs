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
/// Field finding (follow-up to #792/#793): after the A* escape and grid quantization were made
/// length-independent, autorouted paths still kept a short FORCED straight between a pin and the
/// first/last bend — the product owner had to shift it away by hand after every re-route. The
/// manager now runs an automatic collapse pass (<see cref="PinStraightCollapser"/> via
/// <c>CollapseAutoRoutePinLeads</c>) that pulls the bend onto the pin whenever it stays clear of
/// siblings and component bodies. These tests pin that contract on real routed connections.
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
        var (connection, startPin, endPin) = RouteUTurn(300);

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
        var (connectionA, _, _) = RouteUTurn(300);
        var (connectionB, _, _) = RouteUTurn(300);

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
        var (connection, startPin, _) = RouteUTurn(300);

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
        var (connection, _, _) = RouteUTurn(300);

        var path = connection.RoutedPath!;
        path.IsValid.ShouldBeTrue();
        PathIntersectionDetector.HasSelfIntersection(path).ShouldBeFalse();
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

    private static (WaveguideConnection Connection, PhysicalPin StartPin, PhysicalPin EndPin) RouteUTurn(
        double pinSeparationY)
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
        return (connection, startPin, endPin);
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
