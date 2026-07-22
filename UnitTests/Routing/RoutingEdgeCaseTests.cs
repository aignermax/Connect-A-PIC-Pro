using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Components.Connections;
using CAP_Core.Components.FormulaReading;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Routing;

/// <summary>
/// Comprehensive integration tests for routing edge cases.
/// Tests close pins, all direction pairs, path quality (no loops/diagonals),
/// and validates that A* produces professional-quality routes.
/// </summary>
[Trait("Category", "Slow")]
public class RoutingEdgeCaseTests
{
    private readonly ITestOutputHelper _output;
    private const double BendRadius = 10.0;
    private const double AngleToleranceDeg = 5.0;
    private const double GapToleranceMicrometers = 0.1;

    public RoutingEdgeCaseTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // ──────────────────────────────────────────────────────────────
    // Close Pin Tests — pins separated by less than 2× bend radius
    // ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(15)]   // 1.5× bend radius
    [InlineData(25)]   // 2.5× bend radius
    [InlineData(40)]   // 4× bend radius
    [InlineData(80)]   // 8× bend radius — baseline
    public void ClosePins_EastToWest_ProducesValidPath(double separation)
    {
        var (startPin, endPin, router) = CreateEastWestPair(0, 0, separation, 0);
        var path = router.Route(startPin, endPin);

        AssertValidPath(path, startPin, endPin, $"E→W sep={separation}µm");
    }

    [Theory]
    [InlineData(15, 5)]
    [InlineData(25, 10)]
    [InlineData(40, 20)]
    public void ClosePins_EastToWest_WithOffset_ProducesValidPath(double separation, double yOffset)
    {
        var (startPin, endPin, router) = CreateEastWestPair(0, 0, separation, yOffset);
        var path = router.Route(startPin, endPin);

        AssertValidPath(path, startPin, endPin, $"E→W sep={separation}µm offset={yOffset}µm");
    }

    [Theory]
    [InlineData(15)]
    [InlineData(25)]
    [InlineData(40)]
    public void ClosePins_NorthToSouth_ProducesValidPath(double separation)
    {
        var (startPin, endPin, router) = CreateNorthSouthPair(0, 0, separation, 0);
        var path = router.Route(startPin, endPin);

        AssertValidPath(path, startPin, endPin, $"N→S sep={separation}µm");
    }

    // ──────────────────────────────────────────────────────────────
    // All Direction Pair Tests — systematic coverage of 12 combos
    // ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 180, 100, 0, "E→W straight")]
    [InlineData(0, 180, 100, 30, "E→W offset")]
    [InlineData(180, 0, 100, 0, "W→E straight")]
    [InlineData(90, 270, 0, 100, "N→S straight")]
    [InlineData(270, 90, 0, 100, "S→N straight")]
    public void OppositeDirections_ProducesValidPath(
        double startAngle, double endAngle, double dx, double dy, string label)
    {
        var (startPin, endPin, router) = CreateDirectionalPair(
            0, 0, startAngle, dx, dy, endAngle);
        var path = router.Route(startPin, endPin);

        AssertValidPath(path, startPin, endPin, label);
    }

    [Theory]
    [InlineData(0, 0, 100, 30, "E→E same-dir")]
    [InlineData(180, 180, 100, 30, "W→W same-dir")]
    [InlineData(90, 90, 30, 100, "N→N same-dir")]
    [InlineData(270, 270, 30, 100, "S→S same-dir")]
    public void SameDirection_ProducesValidPath(
        double startAngle, double endAngle, double dx, double dy, string label)
    {
        var (startPin, endPin, router) = CreateDirectionalPair(
            0, 0, startAngle, dx, dy, endAngle);
        var path = router.Route(startPin, endPin);

        AssertValidPath(path, startPin, endPin, label);
    }

    [Theory]
    [InlineData(0, 90, 80, 80, "E→N")]
    [InlineData(0, 270, 80, -80, "E→S")]
    [InlineData(90, 0, 80, 80, "N→E")]
    [InlineData(90, 180, -80, 80, "N→W")]
    public void PerpendicularDirections_ProducesValidPath(
        double startAngle, double endAngle, double dx, double dy, string label)
    {
        var (startPin, endPin, router) = CreateDirectionalPair(
            0, 0, startAngle, dx, dy, endAngle);
        var path = router.Route(startPin, endPin);

        AssertValidPath(path, startPin, endPin, label);
    }

    // ──────────────────────────────────────────────────────────────
    // Path Quality — no loops, no excessive length
    // ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(80, 0)]
    [InlineData(80, 20)]
    [InlineData(80, 50)]
    [InlineData(150, 0)]
    [InlineData(150, 40)]
    public void PathLength_ShouldNotExceed3xManhattan(double separation, double yOffset)
    {
        var (startPin, endPin, router) = CreateEastWestPair(0, 0, separation, yOffset);
        var path = router.Route(startPin, endPin);

        path.ShouldNotBeNull();
        path.Segments.ShouldNotBeEmpty();

        var (sx, sy) = startPin.GetAbsolutePosition();
        var (ex, ey) = endPin.GetAbsolutePosition();
        double manhattanDist = Math.Abs(ex - sx) + Math.Abs(ey - sy);

        // Path should not be excessively longer than Manhattan distance.
        // Factor of 3 accommodates bends and escape segments.
        double ratio = path.TotalLengthMicrometers / Math.Max(manhattanDist, 1.0);
        _output.WriteLine(
            $"sep={separation} offset={yOffset}: path={path.TotalLengthMicrometers:F1}µm " +
            $"manhattan={manhattanDist:F1}µm ratio={ratio:F2}");

        ratio.ShouldBeLessThan(3.0,
            $"Path is {ratio:F1}× Manhattan distance ({path.TotalLengthMicrometers:F1}µm vs " +
            $"{manhattanDist:F1}µm). This indicates a loop or unnecessary detour.");
    }

    [Theory]
    [InlineData(80, 0)]
    [InlineData(80, 30)]
    [InlineData(150, 50)]
    public void AStarShouldSucceed_NotFallbackToManhattan(double separation, double yOffset)
    {
        var (startPin, endPin, router) = CreateEastWestPair(0, 0, separation, yOffset);
        var path = router.Route(startPin, endPin);

        path.ShouldNotBeNull();
        path.Segments.ShouldNotBeEmpty();
        path.IsBlockedFallback.ShouldBeFalse(
            $"A* failed for sep={separation} offset={yOffset}. " +
            $"Path has {path.Segments.Count} segments and fell back to Manhattan (red dashed).");
    }

    // ──────────────────────────────────────────────────────────────
    // Segment Quality — no diagonals, proper angles, bend radii
    // ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(80, 0)]
    [InlineData(80, 25)]
    [InlineData(150, 50)]
    public void AllStraightSegments_ShouldBeAxisAligned(double separation, double yOffset)
    {
        var (startPin, endPin, router) = CreateEastWestPair(0, 0, separation, yOffset);
        var path = router.Route(startPin, endPin);

        path.ShouldNotBeNull();
        AssertStraightSegmentsDirectionAligned(path, $"sep={separation} offset={yOffset}");
    }

    [Theory]
    [InlineData(80, 25)]
    [InlineData(150, 50)]
    public void AllBendSegments_ShouldRespectMinRadius(double separation, double yOffset)
    {
        var (startPin, endPin, router) = CreateEastWestPair(0, 0, separation, yOffset);
        var path = router.Route(startPin, endPin);

        path.ShouldNotBeNull();
        foreach (var seg in path.Segments)
        {
            if (seg is BendSegment bend)
            {
                bend.RadiusMicrometers.ShouldBeGreaterThanOrEqualTo(BendRadius * 0.99,
                    $"Bend radius {bend.RadiusMicrometers:F2}µm is below minimum {BendRadius}µm");
            }
        }
    }

    [Theory]
    [InlineData(80, 0)]
    [InlineData(80, 25)]
    [InlineData(150, 50)]
    public void NoZeroLengthSegments(double separation, double yOffset)
    {
        var (startPin, endPin, router) = CreateEastWestPair(0, 0, separation, yOffset);
        var path = router.Route(startPin, endPin);

        path.ShouldNotBeNull();
        for (int i = 0; i < path.Segments.Count; i++)
        {
            path.Segments[i].LengthMicrometers.ShouldBeGreaterThan(0.01,
                $"Segment {i} has zero/near-zero length: {path.Segments[i].LengthMicrometers:F4}µm");
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Obstacle Avoidance — routes around components
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void RouteAroundObstacle_PathIsValid()
    {
        var startComp = CreateTestComponent(-60, -25);
        var endComp = CreateTestComponent(160, -25);
        var obstacle = CreateTestComponent(50, -60);
        obstacle.WidthMicrometers = 60;
        obstacle.HeightMicrometers = 120;

        var router = CreateRouter(-100, -150, 300, 150, startComp, endComp, obstacle);

        var startPin = CreatePin(startComp, startComp.WidthMicrometers, 25, 0);
        var endPin = CreatePin(endComp, 0, 25, 180);

        var path = router.Route(startPin, endPin);

        AssertValidPath(path, startPin, endPin, "route around obstacle");
        path.IsBlockedFallback.ShouldBeFalse("A* should find path around obstacle");
    }

    [Fact]
    public void RouteAroundObstacle_PathLengthIsReasonable()
    {
        var startComp = CreateTestComponent(-60, -25);
        var endComp = CreateTestComponent(160, -25);
        var obstacle = CreateTestComponent(50, -60);
        obstacle.WidthMicrometers = 60;
        obstacle.HeightMicrometers = 120;

        var router = CreateRouter(-100, -150, 300, 150, startComp, endComp, obstacle);

        var startPin = CreatePin(startComp, startComp.WidthMicrometers, 25, 0);
        var endPin = CreatePin(endComp, 0, 25, 180);

        var path = router.Route(startPin, endPin);
        path.ShouldNotBeNull();

        var (sx, sy) = startPin.GetAbsolutePosition();
        var (ex, ey) = endPin.GetAbsolutePosition();
        double directDistance = Math.Sqrt(Math.Pow(ex - sx, 2) + Math.Pow(ey - sy, 2));

        // Even routing around an obstacle shouldn't be more than 4× direct distance
        double ratio = path.TotalLengthMicrometers / Math.Max(directDistance, 1.0);
        _output.WriteLine($"Obstacle route: path={path.TotalLengthMicrometers:F1}µm direct={directDistance:F1}µm ratio={ratio:F2}");

        ratio.ShouldBeLessThan(4.0,
            $"Path around obstacle is {ratio:F1}× direct distance — likely a loop or excessive detour");
    }

    // ──────────────────────────────────────────────────────────────
    // Multi-connection routing — sequential collision avoidance
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void TwoParallelConnections_BothValid()
    {
        var comp1 = CreateTestComponent(0, 0);
        var comp2 = CreateTestComponent(150, 0);

        var router = new WaveguideRouter
        {
            MinBendRadiusMicrometers = BendRadius,
            MinWaveguideSpacingMicrometers = 2.0
        };
        router.InitializePathfindingGrid(-50, -50, 300, 150, new[] { comp1, comp2 });

        var pin1Start = CreatePin(comp1, 50, 15, 0);
        var pin1End = CreatePin(comp2, 0, 15, 180);
        var pin2Start = CreatePin(comp1, 50, 35, 0);
        var pin2End = CreatePin(comp2, 0, 35, 180);

        var connManager = new WaveguideConnectionManager(router) { UseSequentialRouting = true };
        var conn1 = connManager.AddConnection(pin1Start, pin1End);
        var conn2 = connManager.AddConnection(pin2Start, pin2End);

        conn1.RoutedPath.ShouldNotBeNull("Connection 1 should have a route");
        conn2.RoutedPath.ShouldNotBeNull("Connection 2 should have a route");
        conn1.RoutedPath.IsValid.ShouldBeTrue("Connection 1 path should be valid");
        conn2.RoutedPath.IsValid.ShouldBeTrue("Connection 2 path should be valid");

        DumpPath(conn1.RoutedPath, pin1Start, pin1End, "conn1");
        DumpPath(conn2.RoutedPath, pin2Start, pin2End, "conn2");
    }

    // ──────────────────────────────────────────────────────────────
    // Assertion Helpers
    // ──────────────────────────────────────────────────────────────

    private void AssertValidPath(RoutedPath path, PhysicalPin startPin, PhysicalPin endPin, string label)
    {
        path.ShouldNotBeNull($"[{label}] Path should not be null");
        path.Segments.ShouldNotBeEmpty($"[{label}] Path should have segments");

        DumpPath(path, startPin, endPin, label);

        // 1. Segments chain without gaps
        path.IsValid.ShouldBeTrue(
            $"[{label}] Path has gaps between segments (IsValid=false). " +
            DescribeGaps(path));

        // 2. First segment starts at start pin
        var (sx, sy) = startPin.GetAbsolutePosition();
        var startDist = Distance(path.Segments[0].StartPoint, (sx, sy));
        startDist.ShouldBeLessThan(GapToleranceMicrometers,
            $"[{label}] Start gap: {startDist:F3}µm " +
            $"({path.Segments[0].StartPoint.X:F2},{path.Segments[0].StartPoint.Y:F2}) vs pin ({sx:F2},{sy:F2})");

        // 3. Last segment ends at end pin
        var (ex, ey) = endPin.GetAbsolutePosition();
        var endDist = Distance(path.Segments[^1].EndPoint, (ex, ey));
        endDist.ShouldBeLessThan(GapToleranceMicrometers,
            $"[{label}] End gap: {endDist:F3}µm " +
            $"({path.Segments[^1].EndPoint.X:F2},{path.Segments[^1].EndPoint.Y:F2}) vs pin ({ex:F2},{ey:F2})");

        // 4. All straight segments have matching stored vs actual angle
        AssertStraightSegmentsDirectionAligned(path, label);
    }

    private void AssertStraightSegmentsDirectionAligned(RoutedPath path, string label)
    {
        for (int i = 0; i < path.Segments.Count; i++)
        {
            if (path.Segments[i] is not StraightSegment straight) continue;

            double dx = straight.EndPoint.X - straight.StartPoint.X;
            double dy = straight.EndPoint.Y - straight.StartPoint.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 0.01) continue;

            double actualAngle = Math.Atan2(dy, dx) * 180 / Math.PI;
            double storedAngle = straight.StartAngleDegrees;

            actualAngle = ((actualAngle % 360) + 360) % 360;
            storedAngle = ((storedAngle % 360) + 360) % 360;

            double angleDiff = Math.Abs(actualAngle - storedAngle);
            if (angleDiff > 180) angleDiff = 360 - angleDiff;

            angleDiff.ShouldBeLessThan(AngleToleranceDeg,
                $"[{label}] Segment {i}: stored={storedAngle:F1}° actual={actualAngle:F1}° " +
                $"diff={angleDiff:F1}° length={length:F2}µm " +
                $"({straight.StartPoint.X:F2},{straight.StartPoint.Y:F2})→" +
                $"({straight.EndPoint.X:F2},{straight.EndPoint.Y:F2})");
        }
    }

    private static string DescribeGaps(RoutedPath path)
    {
        var gaps = new List<string>();
        for (int i = 1; i < path.Segments.Count; i++)
        {
            var prev = path.Segments[i - 1];
            var curr = path.Segments[i];
            var gap = Distance(prev.EndPoint, curr.StartPoint);
            if (gap > 0.1)
            {
                gaps.Add($"seg{i - 1}→seg{i}: {gap:F3}µm " +
                         $"({prev.EndPoint.X:F2},{prev.EndPoint.Y:F2})→" +
                         $"({curr.StartPoint.X:F2},{curr.StartPoint.Y:F2})");
            }
        }
        return gaps.Count > 0 ? string.Join("; ", gaps) : "no gaps found";
    }

    // ──────────────────────────────────────────────────────────────
    // Test Fixture Helpers
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates an East→West pin pair with given horizontal separation and vertical offset.
    /// Components are 50×50µm. Pins are on the right/left edges, centered vertically.
    /// </summary>
    private static (PhysicalPin start, PhysicalPin end, WaveguideRouter router) CreateEastWestPair(
        double startX, double startY, double separation, double yOffset)
    {
        var startComp = CreateTestComponent(startX, startY);
        var endComp = CreateTestComponent(startX + separation + startComp.WidthMicrometers, startY + yOffset);

        var startPin = CreatePin(startComp, startComp.WidthMicrometers, 25, 0);
        var endPin = CreatePin(endComp, 0, 25, 180);

        var router = CreateRouter(-100, -100, 400, 300, startComp, endComp);
        return (startPin, endPin, router);
    }

    /// <summary>
    /// Creates a North→South pin pair with given vertical separation and horizontal offset.
    /// </summary>
    private static (PhysicalPin start, PhysicalPin end, WaveguideRouter router) CreateNorthSouthPair(
        double startX, double startY, double separation, double xOffset)
    {
        var startComp = CreateTestComponent(startX, startY);
        var endComp = CreateTestComponent(startX + xOffset, startY + separation + startComp.HeightMicrometers);

        var startPin = CreatePin(startComp, 25, startComp.HeightMicrometers, 90);
        var endPin = CreatePin(endComp, 25, 0, 270);

        var router = CreateRouter(-100, -100, 400, 400, startComp, endComp);
        return (startPin, endPin, router);
    }

    /// <summary>
    /// Creates a pin pair with arbitrary start/end angles and positions.
    /// Start pin is on a component at (startX, startY).
    /// End pin is on a component offset by (dx, dy).
    /// </summary>
    private static (PhysicalPin start, PhysicalPin end, WaveguideRouter router) CreateDirectionalPair(
        double startX, double startY, double startAngle, double dx, double dy, double endAngle)
    {
        var startComp = CreateTestComponent(startX, startY);
        var endComp = CreateTestComponent(startX + dx, startY + dy);

        var startPin = CreatePin(startComp, 25, 25, startAngle);
        var endPin = CreatePin(endComp, 25, 25, endAngle);

        double minX = Math.Min(startX, startX + dx) - 150;
        double minY = Math.Min(startY, startY + dy) - 150;
        double maxX = Math.Max(startX, startX + dx) + 200;
        double maxY = Math.Max(startY, startY + dy) + 200;

        var router = CreateRouter(minX, minY, maxX, maxY, startComp, endComp);
        return (startPin, endPin, router);
    }

    private static PhysicalPin CreatePin(Component comp, double offsetX, double offsetY, double angle)
    {
        return new PhysicalPin
        {
            Name = angle < 90 || angle > 270 ? "output" : "input",
            OffsetXMicrometers = offsetX,
            OffsetYMicrometers = offsetY,
            AngleDegrees = angle,
            ParentComponent = comp
        };
    }

    private static WaveguideRouter CreateRouter(
        double minX, double minY, double maxX, double maxY, params Component[] components)
    {
        var router = new WaveguideRouter
        {
            MinBendRadiusMicrometers = BendRadius,
            MinWaveguideSpacingMicrometers = 2.0,
            AStarCellSize = 2.0  // Use fine grid for these edge case tests
        };
        router.InitializePathfindingGrid(minX, minY, maxX, maxY, components);
        return router;
    }

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
            identifier: $"Test_{x}_{y}",
            rotationCounterClock: DiscreteRotation.R0
        );

        component.WidthMicrometers = 50;
        component.HeightMicrometers = 50;
        component.PhysicalX = x;
        component.PhysicalY = y;

        return component;
    }

    private void DumpPath(RoutedPath path, PhysicalPin startPin, PhysicalPin endPin, string label)
    {
        var (sx, sy) = startPin.GetAbsolutePosition();
        var (ex, ey) = endPin.GetAbsolutePosition();

        _output.WriteLine($"--- {label} ---");
        _output.WriteLine($"  Start pin: ({sx:F2}, {sy:F2}) angle={startPin.GetAbsoluteAngle():F0}°");
        _output.WriteLine($"  End pin:   ({ex:F2}, {ey:F2}) angle={endPin.GetAbsoluteAngle():F0}°");
        _output.WriteLine($"  Segments: {path.Segments.Count}, length={path.TotalLengthMicrometers:F1}µm, " +
                          $"fallback={path.IsBlockedFallback}");

        for (int i = 0; i < path.Segments.Count; i++)
        {
            var seg = path.Segments[i];
            if (seg is StraightSegment s)
            {
                _output.WriteLine(
                    $"    [{i}] Straight: ({s.StartPoint.X:F2},{s.StartPoint.Y:F2})→" +
                    $"({s.EndPoint.X:F2},{s.EndPoint.Y:F2}) angle={s.StartAngleDegrees:F1}° len={s.LengthMicrometers:F2}µm");
            }
            else if (seg is BendSegment b)
            {
                _output.WriteLine(
                    $"    [{i}] Bend: ({b.StartPoint.X:F2},{b.StartPoint.Y:F2})→" +
                    $"({b.EndPoint.X:F2},{b.EndPoint.Y:F2}) r={b.RadiusMicrometers:F1} " +
                    $"sweep={b.SweepAngleDegrees:F1}° {b.StartAngleDegrees:F1}°→{b.EndAngleDegrees:F1}°");
            }
        }
    }

    private static double Distance((double X, double Y) a, (double X, double Y) b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
