using CAP_Core.Analysis;
using CAP_Core.Components.Core;
using CAP_Core.Components.Connections;
using CAP_Core.Routing;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis;

public class WaveguideSpacingDetectorCoreTests
{
    private readonly WaveguideSpacingDetector _detector = new();
    private const double MinSpacing = 2.0;

    [Fact]
    public void DetectViolations_NoConnections_ReturnsEmpty()
    {
        var result = _detector.DetectViolations(
            Array.Empty<WaveguideConnection>(),
            Array.Empty<ComponentGroup>(),
            MinSpacing);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void DetectViolations_NullConnections_Throws()
    {
        Should.Throw<ArgumentNullException>(() =>
            _detector.DetectViolations(null!, Array.Empty<ComponentGroup>(), MinSpacing));
    }

    [Fact]
    public void DetectViolations_NullGroups_Throws()
    {
        Should.Throw<ArgumentNullException>(() =>
            _detector.DetectViolations(Array.Empty<WaveguideConnection>(), null!, MinSpacing));
    }

    [Fact]
    public void DetectViolations_ParallelRoutesBelowMinSpacing_ReturnsOneIssue()
    {
        var conn1 = WaveguideSpacingDetectorTestHelpers.CreateConnectionWithSegment(0, 0, 100, 0);
        var conn2 = WaveguideSpacingDetectorTestHelpers.CreateConnectionWithSegment(0, 2.0, 100, 2.0);

        var result = _detector.DetectViolations(
            new[] { conn1, conn2 },
            Array.Empty<ComponentGroup>(),
            MinSpacing);

        result.Count.ShouldBe(1);
        result[0].Type.ShouldBe(DesignIssueType.WaveguideSpacingViolation);
    }

    [Fact]
    public void DetectViolations_ParallelRoutesBelowMinSpacing_DescriptionContainsDistanceAndMinimum()
    {
        var conn1 = WaveguideSpacingDetectorTestHelpers.CreateConnectionWithSegment(0, 0, 100, 0);
        var conn2 = WaveguideSpacingDetectorTestHelpers.CreateConnectionWithSegment(0, 2.0, 100, 2.0);

        var result = _detector.DetectViolations(
            new[] { conn1, conn2 },
            Array.Empty<ComponentGroup>(),
            MinSpacing);

        result[0].Description.ShouldContain("1.50");
        result[0].Description.ShouldContain("2.00");
        result[0].Description.ShouldContain(conn1.StartPin.ParentComponent.Identifier);
        result[0].Description.ShouldContain(conn2.StartPin.ParentComponent.Identifier);
    }

    [Fact]
    public void DetectViolations_ParallelRoutesBelowMinSpacing_PositionedAtClosestApproach()
    {
        var conn1 = WaveguideSpacingDetectorTestHelpers.CreateConnectionWithSegment(0, 0, 100, 0);
        var conn2 = WaveguideSpacingDetectorTestHelpers.CreateConnectionWithSegment(0, 2.0, 100, 2.0);

        var result = _detector.DetectViolations(
            new[] { conn1, conn2 },
            Array.Empty<ComponentGroup>(),
            MinSpacing);

        result[0].X.ShouldBe(50, 0.5);
        result[0].Y.ShouldBe(1.0, 0.1);
    }

    [Fact]
    public void DetectViolations_ParallelRoutesExactlyAtMinSpacing_ReturnsEmpty()
    {
        var conn1 = WaveguideSpacingDetectorTestHelpers.CreateConnectionWithSegment(0, 0, 100, 0);
        var conn2 = WaveguideSpacingDetectorTestHelpers.CreateConnectionWithSegment(0, 2.5, 100, 2.5);

        var result = _detector.DetectViolations(
            new[] { conn1, conn2 },
            Array.Empty<ComponentGroup>(),
            MinSpacing);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void DetectViolations_ParallelRoutesAboveMinSpacing_ReturnsEmpty()
    {
        var conn1 = WaveguideSpacingDetectorTestHelpers.CreateConnectionWithSegment(0, 0, 100, 0);
        var conn2 = WaveguideSpacingDetectorTestHelpers.CreateConnectionWithSegment(0, 4.0, 100, 4.0);

        var result = _detector.DetectViolations(
            new[] { conn1, conn2 },
            Array.Empty<ComponentGroup>(),
            MinSpacing);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void DetectViolations_SameConnectionFold_ReturnsEmpty()
    {
        var connection = WaveguideSpacingDetectorTestHelpers.CreateConnectionWithSegments(
            new StraightSegment(0, 0, 100, 0, 0),
            new StraightSegment(100, 0, 100, 2.0, 90),
            new StraightSegment(100, 2.0, 0, 2.0, 180));

        var result = _detector.DetectViolations(
            new[] { connection },
            Array.Empty<ComponentGroup>(),
            MinSpacing);

        result.ShouldBeEmpty("segments of the same connection are exempt from spacing checks");
    }

    [Fact]
    public void DetectViolations_AdjacentSegmentsSharingJoint_ReturnsEmpty()
    {
        var conn1 = WaveguideSpacingDetectorTestHelpers.CreateConnectionWithSegment(0, 0, 100, 0);
        var conn2 = WaveguideSpacingDetectorTestHelpers.CreateConnectionWithSegment(100, 0, 100, 50);

        var result = _detector.DetectViolations(
            new[] { conn1, conn2 },
            Array.Empty<ComponentGroup>(),
            MinSpacing);

        result.ShouldBeEmpty("segments that meet at a joint are exempt");
    }

    [Fact]
    public void DetectViolations_CrossingRoutes_DoesNotReportSpacingIssue()
    {
        var conn1 = WaveguideSpacingDetectorTestHelpers.CreateConnectionWithSegment(0, 50, 100, 50);
        var conn2 = WaveguideSpacingDetectorTestHelpers.CreateConnectionWithSegment(50, 0, 50, 100);

        var result = _detector.DetectViolations(
            new[] { conn1, conn2 },
            Array.Empty<ComponentGroup>(),
            MinSpacing);

        result.ShouldBeEmpty("overlaps are handled by the overlap detector, not spacing");
    }

    [Fact]
    public void DetectViolations_FrozenPathNearLiveRoute_ReturnsIssue()
    {
        var conn = WaveguideSpacingDetectorTestHelpers.CreateConnectionWithSegment(0, 0, 100, 0);
        var group = WaveguideSpacingDetectorTestHelpers.CreateGroupWithFrozenPath(0, 2.0, 100, 2.0);

        var result = _detector.DetectViolations(
            new[] { conn },
            new[] { group },
            MinSpacing);

        result.Count.ShouldBe(1);
        result[0].Type.ShouldBe(DesignIssueType.WaveguideSpacingViolation);
        result[0].Connection.ShouldBe(conn);
    }

    [Fact]
    public void DetectViolations_FrozenPathFarFromLiveRoute_ReturnsEmpty()
    {
        var conn = WaveguideSpacingDetectorTestHelpers.CreateConnectionWithSegment(0, 0, 100, 0);
        var group = WaveguideSpacingDetectorTestHelpers.CreateGroupWithFrozenPath(0, 10.0, 100, 10.0);

        var result = _detector.DetectViolations(
            new[] { conn },
            new[] { group },
            MinSpacing);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void DetectViolations_LargeNumberOfSegments_CompletesQuickly()
    {
        const int segmentCount = 400;
        var connections = new List<WaveguideConnection>(segmentCount);

        for (int i = 0; i < segmentCount; i++)
        {
            double y = i * MinSpacing * 2.0;
            connections.Add(WaveguideSpacingDetectorTestHelpers.CreateConnectionWithSegment(0, y, 100, y));
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = _detector.DetectViolations(
            connections,
            Array.Empty<ComponentGroup>(),
            MinSpacing);
        sw.Stop();

        result.ShouldBeEmpty();
        sw.ElapsedMilliseconds.ShouldBeLessThan(10_000,
            $"spacing check with {segmentCount} segments took {sw.ElapsedMilliseconds} ms");
    }
}
