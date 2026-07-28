using CAP_Core.Components.Connections;
using CAP_Core.Routing;
using CAP_Core.Routing.CrossingInsertion.ManualCrossing;
using Shouldly;
using UnitTests.Routing.CrossingInsertion;
using Xunit;

namespace UnitTests.Routing.CrossingInsertion.ManualCrossing;

/// <summary>
/// Tests for the Cut tool's free-cut fallback: when the pointer is not within snap range of
/// any guide-intersection candidate, clicking directly on a cuttable straight segment still
/// works, projected onto the pointer's nearest point on that segment.
/// </summary>
public class ManualCrossingFreeCandidateTests
{
    private const double RequiredRunMicrometers = 10.0;

    private readonly ManualCrossingCandidateFinder _finder = new();

    [Fact]
    public void TryCreateFreeCandidate_ProjectsOffAxisPointOntoSegment()
    {
        var connection = CreateHorizontalConnection();
        var segment = (StraightSegment)connection.GetPathSegments()[0];

        var candidate = _finder.TryCreateFreeCandidate(connection, segment, (200.0, 130.0), RequiredRunMicrometers);

        candidate.ShouldNotBeNull();
        candidate!.IntersectionPoint.ShouldBe((200.0, 100.0));
        candidate.IsFreeCut.ShouldBeTrue();
        candidate.GuideLine.ShouldBeNull();
        candidate.SegmentIsHorizontal.ShouldBeTrue();
    }

    [Fact]
    public void TryCreateFreeCandidate_ClampsToEndpoint_ThenFailsStraightRunGuard()
    {
        var connection = CreateHorizontalConnection(); // (10,100) -> (390,100)
        var segment = (StraightSegment)connection.GetPathSegments()[0];

        // Beyond the segment's end — the clamped projection lands exactly on the endpoint,
        // which then has zero straight run on that side and is safely rejected.
        var candidate = _finder.TryCreateFreeCandidate(connection, segment, (500.0, 100.0), RequiredRunMicrometers);

        candidate.ShouldBeNull();
    }

    [Fact]
    public void TryCreateFreeCandidate_RejectsDiagonalSegment()
    {
        var left = CrossingTestCircuit.CreateTerminal("diag-left", 0, 0, pinAngleDegrees: 0);
        var right = CrossingTestCircuit.CreateTerminal("diag-right", 300, 300, pinAngleDegrees: 180);
        var connection = new WaveguideConnection { StartPin = left.PhysicalPin, EndPin = right.PhysicalPin };
        var path = new RoutedPath();
        var diagonal = new StraightSegment(0, 0, 300, 300, 45);
        path.Segments.Add(diagonal);
        connection.RestoreCachedPath(path);

        var candidate = _finder.TryCreateFreeCandidate(connection, diagonal, (150.0, 150.0), RequiredRunMicrometers);

        candidate.ShouldBeNull();
    }

    [Fact]
    public void TryCreateFreeCandidate_RejectsWhenTooCloseToSegmentEnd()
    {
        var connection = CreateHorizontalConnection(); // (10,100) -> (390,100)

        // 5 µm from the left end — less than the required 10 µm straight run.
        var candidate = _finder.TryCreateFreeCandidate(
            connection, (StraightSegment)connection.GetPathSegments()[0], (15.0, 100.0), RequiredRunMicrometers);

        candidate.ShouldBeNull();
    }

    [Fact]
    public void FindNearestFreeCandidate_PicksNearestSegmentWithinRadius()
    {
        var near = CreateHorizontalConnection(); // y = 100
        var far = CreateRoutedConnectionAt(y: 250);

        var candidate = _finder.FindNearestFreeCandidate(
            new[] { near, far }, (200.0, 105.0), maxDistanceMicrometers: 20.0, RequiredRunMicrometers);

        candidate.ShouldNotBeNull();
        candidate!.Connection.ShouldBeSameAs(near);
        candidate.IntersectionPoint.ShouldBe((200.0, 100.0));
    }

    [Fact]
    public void FindNearestFreeCandidate_NoSegmentWithinRadius_ReturnsNull()
    {
        var connection = CreateHorizontalConnection();

        var candidate = _finder.FindNearestFreeCandidate(
            new[] { connection }, (200.0, 300.0), maxDistanceMicrometers: 20.0, RequiredRunMicrometers);

        candidate.ShouldBeNull();
    }

    [Fact]
    public void FindNearestFreeCandidate_RejectsWithoutFallingBackToFartherSegment()
    {
        // Two candidate segments near the click: the nearest (directly under the pointer) has
        // no straight run left; the search must not then try the farther, roomy one instead —
        // the click was clearly aimed at the cramped spot.
        var cramped = CreateRoutedConnectionAt(y: 100, startX: 190, endX: 205);
        var roomy = CreateRoutedConnectionAt(y: 130);

        var candidate = _finder.FindNearestFreeCandidate(
            new[] { cramped, roomy }, (198.0, 100.0), maxDistanceMicrometers: 40.0, RequiredRunMicrometers);

        candidate.ShouldBeNull();
    }

    [Fact]
    public void FindNearestFreeCandidate_SkipsBendSegments_ArcsExcluded()
    {
        var start = CrossingTestCircuit.CreateTerminal("arc-start", 0, 95, pinAngleDegrees: 0);
        var end = CrossingTestCircuit.CreateTerminal("arc-end", 195, 10, pinAngleDegrees: 90);
        var connection = new WaveguideConnection { StartPin = start.PhysicalPin, EndPin = end.PhysicalPin };
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(10, 100, 150, 100, 0));
        path.Segments.Add(new BendSegment(150, 90, 10, 0, -90));
        path.Segments.Add(new StraightSegment(200, 100, 200, 20, 270));
        connection.RestoreCachedPath(path);

        // Near the bend, far from either straight run — only StraightSegments are considered.
        var candidate = _finder.FindNearestFreeCandidate(
            new[] { connection }, (160.0, 90.0), maxDistanceMicrometers: 5.0, RequiredRunMicrometers);

        candidate.ShouldBeNull();
    }

    [Fact]
    public void ResolveCandidate_SnapTakesPrecedenceOverFreeCut_WhenBothInRange()
    {
        var connection = CreateHorizontalConnection();
        var guideTerminal = CrossingTestCircuit.CreateTerminal("guide", 195, 40, pinAngleDegrees: 90);
        var staticCandidate = new ManualCrossingCandidate(
            connection, (StraightSegment)connection.GetPathSegments()[0],
            new PinGuideLine(guideTerminal.PhysicalPin, (200.0, 50.0), (0.0, 1.0), IsHorizontal: false),
            (200.0, 100.0), SegmentIsHorizontal: true, SegmentDirection: (1.0, 0.0));

        var resolved = _finder.ResolveCandidate(
            new[] { staticCandidate }, new[] { connection }, (201.0, 101.0),
            snapRadiusMicrometers: 10.0, RequiredRunMicrometers);

        resolved.ShouldBeSameAs(staticCandidate);
        resolved!.IsFreeCut.ShouldBeFalse();
    }

    [Fact]
    public void ResolveCandidate_FallsBackToFreeCut_WhenNoStaticCandidateInRange()
    {
        var connection = CreateHorizontalConnection();

        var resolved = _finder.ResolveCandidate(
            Array.Empty<ManualCrossingCandidate>(), new[] { connection }, (300.0, 100.0),
            snapRadiusMicrometers: 10.0, RequiredRunMicrometers);

        resolved.ShouldNotBeNull();
        resolved!.IsFreeCut.ShouldBeTrue();
        resolved.IntersectionPoint.ShouldBe((300.0, 100.0));
    }

    [Fact]
    public void ResolveCandidate_ReturnsNull_WhenNeitherSnapNorSegmentInRange()
    {
        var connection = CreateHorizontalConnection();

        var resolved = _finder.ResolveCandidate(
            Array.Empty<ManualCrossingCandidate>(), new[] { connection }, (200.0, 500.0),
            snapRadiusMicrometers: 10.0, RequiredRunMicrometers);

        resolved.ShouldBeNull();
    }

    /// <summary>Horizontal net (10,100)→(390,100) with an injected straight route.</summary>
    private static WaveguideConnection CreateHorizontalConnection() => CreateRoutedConnectionAt(y: 100);

    /// <summary>A connection with a single injected straight route from (startX,y) to (endX,y).</summary>
    private static WaveguideConnection CreateRoutedConnectionAt(double y, double startX = 10, double endX = 390)
    {
        string suffix = $"{y}-{startX}-{endX}";
        var left = CrossingTestCircuit.CreateTerminal($"left-{suffix}", startX - 10, y - 5, pinAngleDegrees: 0);
        var right = CrossingTestCircuit.CreateTerminal($"right-{suffix}", endX + 10, y - 5, pinAngleDegrees: 180);
        var connection = new WaveguideConnection { StartPin = left.PhysicalPin, EndPin = right.PhysicalPin };
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(startX, y, endX, y, 0));
        connection.RestoreCachedPath(path);
        return connection;
    }
}
