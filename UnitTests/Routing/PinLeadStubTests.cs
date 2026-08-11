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
/// Field finding (follow-up to the #791/#792 segment-shift work): routes used to keep a long
/// FORCED straight lead between a pin and the first bend. The A* pin escape scaled with the
/// pin separation (up to 15 cells = 60 µm), the Manhattan fallback added a cosmetic
/// 0.15 × radius lead — so different connections kept visibly different distances from their
/// components. The GDS export writes exact segments with absolute placement, so no straight
/// lead is required there: the first bend may begin directly at the pin (tangentially).
/// These tests pin the new contract: the forced lead is bounded by the bend geometry plus
/// grid quantization, independent of connection length, and the segment shift can collapse
/// the remaining lead to exactly zero.
/// </summary>
public class PinLeadStubTests
{
    private const double Radius = 10.0;

    /// <summary>Upper bound for the pin-side lead: one tangent-fitting quantization step of
    /// slack (2 grid cells) — NOT dependent on how far the pins are apart.</summary>
    private static double MaxLead(WaveguideRouter router) => 2 * router.AStarCellSize;

    /// <summary>
    /// U-turn layout: both pins face east, so the route must leave the start pin east, turn
    /// around and come back west into the end pin. Every extra micron traveled east adds pure
    /// path length, so the cost-optimal A* route turns as early as the pin-escape constraint
    /// allows — the first-bend distance directly measures the FORCED lead, deterministically.
    /// </summary>
    private static (WaveguideRouter Router, PhysicalPin StartPin, PhysicalPin EndPin) UTurnSetup(
        double pinSeparationY)
    {
        var router = CreateRouter();
        var start = CreateTestComponent(0, 0);
        var end = CreateTestComponent(0, pinSeparationY - 25);
        router.InitializePathfindingGrid(-100, -100, 500, 500, new[] { start, end });

        var startPin = Pin(start, 50, 25, 0);   // right edge, pointing east
        var endPin = Pin(end, 50, 25, 0);       // right edge, pointing east (arrival heading west)
        return (router, startPin, endPin);
    }

    [Theory]
    [InlineData(150.0)]  // short connection
    [InlineData(300.0)]  // long connection — used to get a 12-cell (48 µm) forced escape
    public void AutoRoute_FirstBendStartsNearStartPin_IndependentOfConnectionLength(double pinSeparationY)
    {
        var (router, startPin, endPin) = UTurnSetup(pinSeparationY);

        var path = router.Route(startPin, endPin);

        path.IsValid.ShouldBeTrue();
        path.Segments.OfType<BendSegment>().ShouldNotBeEmpty("a U-turn route needs bends");

        double lead = StartPinLeadLength(path, startPin);
        lead.ShouldBeLessThanOrEqualTo(MaxLead(router),
            $"forced straight lead at the start pin must not exceed grid quantization (was {lead:F1} µm)");
    }

    [Fact]
    public void AutoRoute_FirstBendIsTangentialToThePinDirection()
    {
        var (router, startPin, endPin) = UTurnSetup(300);

        var path = router.Route(startPin, endPin);

        path.IsValid.ShouldBeTrue();
        var firstBend = path.Segments.OfType<BendSegment>().First();
        // Tangency at the pin: the first bend continues the pin heading (0°). This is the
        // physical requirement that stays — only the forced straight length is gone.
        NormalizeAngle(firstBend.StartAngleDegrees).ShouldBe(0, 1e-6);
    }

    /// <summary>
    /// Field precision: the forced straight was observed at the ARRIVAL side — the route's
    /// final straight before the end pin. The arrival must get exactly the same
    /// radius-derived bound as the departure (the A* goal lock used to hold the last corner
    /// one cell further out than the start escape held the first).
    /// </summary>
    [Fact]
    public void AutoRoute_LastBendEndsNearEndPin()
    {
        var (router, startPin, endPin) = UTurnSetup(300);

        var path = router.Route(startPin, endPin);

        path.IsValid.ShouldBeTrue();
        double lead = EndPinLeadLength(path, endPin);
        lead.ShouldBeLessThanOrEqualTo(MaxLead(router),
            $"forced straight lead at the end pin must not exceed grid quantization (was {lead:F1} µm)");
    }

    /// <summary>
    /// Field hypothesis check: the direction of the connection must NOT decide which end
    /// keeps a stub. Routing the same U-turn in both directions yields the same small
    /// departure AND arrival leads on both ends.
    /// </summary>
    [Fact]
    public void AutoRoute_DepartureAndArrivalLeads_AreSymmetric_RegardlessOfDirection()
    {
        var (router, pinA, pinB) = UTurnSetup(300);

        var forward = router.Route(pinA, pinB);
        var backward = router.Route(pinB, pinA);

        forward.IsValid.ShouldBeTrue();
        backward.IsValid.ShouldBeTrue();
        double bound = MaxLead(router);
        StartPinLeadLength(forward, pinA).ShouldBeLessThanOrEqualTo(bound, "A→B departure lead");
        EndPinLeadLength(forward, pinB).ShouldBeLessThanOrEqualTo(bound, "A→B arrival lead");
        StartPinLeadLength(backward, pinB).ShouldBeLessThanOrEqualTo(bound, "B→A departure lead");
        EndPinLeadLength(backward, pinA).ShouldBeLessThanOrEqualTo(bound, "B→A arrival lead");
    }

    [Fact]
    public void ManhattanFallback_FirstBendStartsDirectlyAtStartPin()
    {
        // No pathfinding grid: Route() goes straight to the Manhattan (CSC) fallback.
        var router = CreateRouter();
        var start = CreateTestComponent(0, 0);
        var end = CreateTestComponent(100, 100);

        var startPin = Pin(start, 25, 50, 90);   // top edge, pointing up
        var endPin = Pin(end, 0, 25, 180);       // left edge, pointing left

        var path = router.Route(startPin, endPin);

        path.IsValid.ShouldBeTrue();
        var (sx, sy) = startPin.GetAbsolutePosition();
        var first = path.Segments[0];
        first.ShouldBeOfType<BendSegment>("the CSC route is tangential by construction — " +
            "no cosmetic straight lead before the first arc");
        first.StartPoint.X.ShouldBe(sx, 1e-6);
        first.StartPoint.Y.ShouldBe(sy, 1e-6);
    }

    [Fact]
    public void ManhattanFallback_StillReachesBothPinsExactly()
    {
        var router = CreateRouter();
        var start = CreateTestComponent(0, 0);
        var end = CreateTestComponent(100, 100);

        var startPin = Pin(start, 25, 50, 90);
        var endPin = Pin(end, 0, 25, 180);

        var path = router.Route(startPin, endPin);

        path.IsValid.ShouldBeTrue();
        var (sx, sy) = startPin.GetAbsolutePosition();
        var (ex, ey) = endPin.GetAbsolutePosition();
        path.Segments[0].StartPoint.X.ShouldBe(sx, 1e-6);
        path.Segments[0].StartPoint.Y.ShouldBe(sy, 1e-6);
        path.Segments[^1].EndPoint.X.ShouldBe(ex, 1e-6);
        path.Segments[^1].EndPoint.Y.ShouldBe(ey, 1e-6);
    }

    /// <summary>
    /// #792 interplay: the segment-shift clamp boundary IS "bend directly at the pin".
    /// Shifting the middle straight by exactly the pin lead's length collapses the lead to
    /// zero length — accepted, and the first bend then begins at the pin.
    /// </summary>
    [Fact]
    public void SegmentShift_CanCollapseThePinLead_SoTheBendStartsAtThePin()
    {
        var conn = new WaveguideConnection();
        conn.RestoreCachedPath(InterconnectRouting.SegmentShiftGeometryTests.ZPath());

        // The incoming straight (pin lead) is 50 µm long; shifting by exactly 50 collapses it.
        SegmentShiftEditor.TryApplyShift(conn, 1, 50, out var error).ShouldBeTrue(error);

        var segments = conn.RoutedPath!.Segments;
        var lead = (StraightSegment)segments[0];
        lead.LengthMicrometers.ShouldBe(0, 1e-6);
        segments[1].ShouldBeOfType<BendSegment>().StartPoint.ShouldBe((0.0, 0.0));
    }

    /// <summary>Length of the straight lead between the start pin and the first bend
    /// (0 when the path begins with a bend).</summary>
    private static double StartPinLeadLength(RoutedPath path, PhysicalPin startPin)
    {
        var (sx, sy) = startPin.GetAbsolutePosition();
        var firstBend = path.Segments.OfType<BendSegment>().First();
        return Distance(sx, sy, firstBend.StartPoint.X, firstBend.StartPoint.Y);
    }

    /// <summary>Length of the straight lead between the last bend and the end pin.</summary>
    private static double EndPinLeadLength(RoutedPath path, PhysicalPin endPin)
    {
        var (ex, ey) = endPin.GetAbsolutePosition();
        var lastBend = path.Segments.OfType<BendSegment>().Last();
        return Distance(ex, ey, lastBend.EndPoint.X, lastBend.EndPoint.Y);
    }

    private static double Distance(double x1, double y1, double x2, double y2)
    {
        double dx = x2 - x1;
        double dy = y2 - y1;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double NormalizeAngle(double degrees)
    {
        double a = degrees % 360.0;
        if (a < 0) a += 360.0;
        return a;
    }

    /// <summary>Router with deterministic settings: cardinal-only turns make the first bend a
    /// 90° arc whose tangent length equals the bend radius. Direct-first is disabled because
    /// these tests target the pin-lead stubs of the A* grid pipeline specifically.</summary>
    private static WaveguideRouter CreateRouter() => new()
    {
        MinBendRadiusMicrometers = Radius,
        MinWaveguideSpacingMicrometers = 2.0,
        UseDiagonalRouting = false,
        PreferDirectStyledRoutes = false,
    };

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

        var component = new Component(
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

        return component;
    }
}
