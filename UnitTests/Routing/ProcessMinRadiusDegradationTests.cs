using CAP_Core.Analysis;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Routing;
using Shouldly;
using Xunit;

namespace UnitTests.Routing;

/// <summary>
/// Regression tests for AUTO routing under a process bend-radius floor (e.g. Cornerstone
/// SiN, 30 µm). After PR #756 the floor fed the router but not its A* cost model, so every
/// route silently fell back to the Manhattan (CSC) router: parallel connections overlapped
/// and crossed, and close pins produced full-circle self-intersecting loops. The router now
/// genuinely attempts A* at the floor and, when no clean path exists, degrades in a
/// controlled way to the connection radius while flagging the route
/// (<see cref="RoutedPath.ViolatesProcessMinBendRadius"/>) for the design checks.
/// </summary>
public class ProcessMinRadiusDegradationTests
{
    private const double CornerstoneSinMinRadius = 30.0;

    /// <summary>Minimum clearance (µm) two distinct routes must keep (waveguide spacing).</summary>
    private const double MinRouteClearance = 2.0;

    [Theory]
    [InlineData(0.0)]
    [InlineData(CornerstoneSinMinRadius)]
    public void ParallelCouplerConnections_NeverCrossEachOther(double processFloor)
    {
        var (manager, inner, outer) = RouteParallelCouplers(processFloor);

        inner.IsPathValid.ShouldBeTrue();
        outer.IsPathValid.ShouldBeTrue();
        inner.IsBlockedFallback.ShouldBeFalse();
        outer.IsBlockedFallback.ShouldBeFalse();

        PathIntersectionDetector.HasSelfIntersection(inner.RoutedPath!).ShouldBeFalse();
        PathIntersectionDetector.HasSelfIntersection(outer.RoutedPath!).ShouldBeFalse();
        PathIntersectionDetector.MinimumDistance(inner.RoutedPath!, outer.RoutedPath!)
            .ShouldBeGreaterThan(MinRouteClearance);

        manager.Connections.Count.ShouldBe(2);
    }

    [Fact]
    public void ParallelCouplers_WithRoomForTheFloor_AllBendsHonorTheProcessMinimum()
    {
        var (_, inner, outer) = RouteParallelCouplers(CornerstoneSinMinRadius);

        foreach (var connection in new[] { inner, outer })
        {
            var bends = connection.GetPathSegments().OfType<BendSegment>().ToList();
            bends.ShouldNotBeEmpty();
            bends.ShouldAllBe(b => b.RadiusMicrometers >= CornerstoneSinMinRadius - 1e-6);
            connection.RoutedPath!.ViolatesProcessMinBendRadius.ShouldBeFalse();
        }
    }

    [Fact]
    public void TightNeighbors_FloorFindsNoPath_DegradesToConnectionRadiusWithViolationFlag()
    {
        var connection = RouteTightNeighbors(CornerstoneSinMinRadius);

        connection.IsPathValid.ShouldBeTrue();
        connection.RoutedPath!.ViolatesProcessMinBendRadius.ShouldBeTrue();
        PathIntersectionDetector.HasSelfIntersection(connection.RoutedPath).ShouldBeFalse();

        // The degraded route still honors the connection's own 10 µm radius.
        var bends = connection.GetPathSegments().OfType<BendSegment>().ToList();
        bends.ShouldNotBeEmpty();
        bends.ShouldAllBe(b => b.RadiusMicrometers >= connection.BendRadiusMicrometers - 1e-6);
    }

    [Fact]
    public void TightNeighbors_NoFloor_RoutesCleanWithoutViolationFlag()
    {
        var connection = RouteTightNeighbors(processFloor: 0.0);

        connection.IsPathValid.ShouldBeTrue();
        connection.RoutedPath!.ViolatesProcessMinBendRadius.ShouldBeFalse();
        PathIntersectionDetector.HasSelfIntersection(connection.RoutedPath).ShouldBeFalse();
    }

    [Fact]
    public void DesignValidator_SurfacesTheProcessMinimumViolation()
    {
        var connection = RouteTightNeighbors(CornerstoneSinMinRadius);

        var issues = new DesignValidator().Validate(new[] { connection });

        var issue = issues.ShouldHaveSingleItem();
        issue.Type.ShouldBe(DesignIssueType.BendRadiusBelowProcessMinimum);
        issue.Connection.ShouldBe(connection);
        issue.Description.ShouldContain("below process minimum");
    }

    /// <summary>
    /// Two stacked 4-pin couplers with two nested connections on the right side
    /// (inner pins together, outer pins together) — the layout from the bug report.
    /// </summary>
    private static (WaveguideConnectionManager Manager, WaveguideConnection Inner, WaveguideConnection Outer)
        RouteParallelCouplers(double processFloor)
    {
        var top = TestComponentFactory.CreateFourPinCouplerWithPhysicalPins("top");
        top.PhysicalX = 60;
        top.PhysicalY = 60;
        var bottom = TestComponentFactory.CreateFourPinCouplerWithPhysicalPins("bottom");
        bottom.PhysicalX = 60;
        bottom.PhysicalY = 420;

        var router = new WaveguideRouter { ProcessMinBendRadiusMicrometers = processFloor };
        router.InitializePathfindingGrid(-100, -100, 1100, 900, new[] { top, bottom });

        var manager = new WaveguideConnectionManager(router);
        var inner = new WaveguideConnection
        {
            StartPin = Pin(top, "east1"),
            EndPin = Pin(bottom, "east0"),
        };
        var outer = new WaveguideConnection
        {
            StartPin = Pin(top, "east0"),
            EndPin = Pin(bottom, "east1"),
        };
        manager.AddExistingConnection(inner);
        manager.AddExistingConnection(outer);
        manager.RecalculateAllTransmissions();
        return (manager, inner, outer);
    }

    /// <summary>
    /// Two components whose facing pins are only ~40 µm apart in X and Y — too tight for a
    /// 30 µm bend radius, the layout that historically produced full-circle loops.
    /// </summary>
    private static WaveguideConnection RouteTightNeighbors(double processFloor)
    {
        var left = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        left.PhysicalX = 60;
        left.PhysicalY = 60;
        var right = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        right.PhysicalX = 350;
        right.PhysicalY = 100;

        var router = new WaveguideRouter { ProcessMinBendRadiusMicrometers = processFloor };
        router.InitializePathfindingGrid(-100, -100, 1100, 900, new[] { left, right });

        var manager = new WaveguideConnectionManager(router);
        var connection = new WaveguideConnection
        {
            StartPin = Pin(left, "out"),
            EndPin = Pin(right, "in"),
        };
        manager.AddExistingConnection(connection);
        manager.RecalculateAllTransmissions();
        return connection;
    }

    private static PhysicalPin Pin(Component component, string name) =>
        component.PhysicalPins.First(p => p.Name == name);
}
