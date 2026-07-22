using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Routing;
using Shouldly;
using Xunit;

namespace UnitTests.Routing;

/// <summary>
/// AUTO-routing regression tests with realistically FLAT components (Cornerstone SiN:
/// "Coupler Straight" 20 × 2.636 µm with a 1.436 µm pin pitch, "Straight" 10 × 1.2 µm)
/// under a process bend-radius floor. Flat components stress the router differently from
/// the tall test couplers: the pin pitch is far below the grid cell size and the waveguide
/// obstacle width, and pin corridors are wider than the whole component body. Routes must
/// never cross their sibling, loop through themselves, or cut through a component body.
/// </summary>
public class FlatComponentProcessMinRadiusTests
{
    private const double CornerstoneSinMinRadius = 30.0;

    /// <summary>Routes must clear each other by more than sampling noise (µm); the
    /// 1.436 µm pin pitch makes the classic 2 µm waveguide clearance unreachable near pins.</summary>
    private const double MinRouteClearance = 0.5;

    /// <summary>Rectangle shrink tolerance (µm) so pins sitting ON the component edge
    /// do not count as penetration.</summary>
    private const double BoundsTolerance = 0.3;

    [Theory]
    [InlineData(200.0, 10.0)]
    [InlineData(200.0, 30.0)]
    [InlineData(80.0, 10.0)]
    [InlineData(80.0, 30.0)]
    [InlineData(30.0, 10.0)]
    [InlineData(30.0, 30.0)]
    public void FlatParallelCouplers_NestedConnectionsStayCleanAndOutsideComponents(
        double gapMicrometers, double processFloor)
    {
        var (top, bottom, inner, outer) = RouteFlatParallelCouplers(gapMicrometers, processFloor);

        inner.IsPathValid.ShouldBeTrue("inner connection must route");
        outer.IsPathValid.ShouldBeTrue("outer connection must route");
        inner.IsBlockedFallback.ShouldBeFalse("inner connection must not be a blocked fallback");
        outer.IsBlockedFallback.ShouldBeFalse("outer connection must not be a blocked fallback");

        PathIntersectionDetector.HasSelfIntersection(inner.RoutedPath!).ShouldBeFalse(
            "inner route must not loop through itself");
        PathIntersectionDetector.HasSelfIntersection(outer.RoutedPath!).ShouldBeFalse(
            "outer route must not loop through itself");
        PathIntersectionDetector.MinimumDistance(inner.RoutedPath!, outer.RoutedPath!)
            .ShouldBeGreaterThan(MinRouteClearance, "nested parallel routes must never cross");

        AssertOutsideComponentBounds(inner, top, bottom);
        AssertOutsideComponentBounds(outer, top, bottom);
    }

    [Fact]
    public void FlatParallelCouplers_WideGap_HonorsTheProcessFloorWithoutViolationFlag()
    {
        var (_, _, inner, outer) = RouteFlatParallelCouplers(gapMicrometers: 200.0, CornerstoneSinMinRadius);

        foreach (var connection in new[] { inner, outer })
        {
            connection.RoutedPath!.ViolatesProcessMinBendRadius.ShouldBeFalse(
                "200 µm of space leaves room for 30 µm bends");
            var bends = connection.GetPathSegments().OfType<BendSegment>().ToList();
            bends.ShouldNotBeEmpty();
            bends.ShouldAllBe(b => b.RadiusMicrometers >= CornerstoneSinMinRadius - 1e-6);
        }
    }

    [Fact]
    public void FlatStraights_OffsetNeighbors_RouteCleanAtTheFloor()
    {
        var connection = RouteFlatStraights(
            leftX: 60, leftY: 60, rightX: 150, rightY: 100, CornerstoneSinMinRadius,
            out var left, out var right);

        connection.IsPathValid.ShouldBeTrue();
        connection.IsBlockedFallback.ShouldBeFalse();
        PathIntersectionDetector.HasSelfIntersection(connection.RoutedPath!).ShouldBeFalse();
        AssertOutsideComponentBounds(connection, left, right);
    }

    [Fact]
    public void FlatStraights_TightNeighbors_DegradeControlledWithoutLoopsOrPenetration()
    {
        var connection = RouteFlatStraights(
            leftX: 60, leftY: 60, rightX: 100, rightY: 70, CornerstoneSinMinRadius,
            out var left, out var right);

        connection.IsPathValid.ShouldBeTrue();
        connection.IsBlockedFallback.ShouldBeFalse();
        PathIntersectionDetector.HasSelfIntersection(connection.RoutedPath!).ShouldBeFalse(
            "tight flat neighbors must not produce loops");
        AssertOutsideComponentBounds(connection, left, right);

        // 30 µm from pin to pin cannot fit 30 µm arcs — a controlled degradation to the
        // connection radius with the violation flag is the physically honest outcome.
        var bends = connection.GetPathSegments().OfType<BendSegment>().ToList();
        bends.ShouldAllBe(b => b.RadiusMicrometers >= connection.BendRadiusMicrometers - 1e-6);
    }

    /// <summary>Asserts no sampled part of the route enters either component body.</summary>
    private static void AssertOutsideComponentBounds(
        WaveguideConnection connection, Component first, Component second)
    {
        foreach (var component in new[] { first, second })
        {
            PathIntersectionDetector.IntersectsRectangle(
                    connection.RoutedPath!,
                    component.PhysicalX + BoundsTolerance,
                    component.PhysicalY + BoundsTolerance,
                    component.PhysicalX + component.WidthMicrometers - BoundsTolerance,
                    component.PhysicalY + component.HeightMicrometers - BoundsTolerance)
                .ShouldBeFalse($"route must not pass through component '{component.Identifier}'");
        }
    }

    /// <summary>
    /// Two flat couplers stacked vertically with the given free gap between their bodies.
    /// Nested connections on the right side: the LOWER right pin of the top component to the
    /// UPPER right pin of the bottom one (inner), and the upper-top to lower-bottom (outer).
    /// </summary>
    private static (Component Top, Component Bottom, WaveguideConnection Inner, WaveguideConnection Outer)
        RouteFlatParallelCouplers(double gapMicrometers, double processFloor)
    {
        var top = TestComponentFactory.CreateFlatCouplerWithPhysicalPins("top");
        top.PhysicalX = 60;
        top.PhysicalY = 60;
        var bottom = TestComponentFactory.CreateFlatCouplerWithPhysicalPins("bottom");
        bottom.PhysicalX = 60;
        bottom.PhysicalY = top.PhysicalY + top.HeightMicrometers + gapMicrometers;

        var router = new WaveguideRouter { ProcessMinBendRadiusMicrometers = processFloor };
        router.InitializePathfindingGrid(-100, -100, 1100, 900, new[] { top, bottom });

        var manager = new WaveguideConnectionManager(router);
        var inner = new WaveguideConnection { StartPin = Pin(top, "east1"), EndPin = Pin(bottom, "east0") };
        var outer = new WaveguideConnection { StartPin = Pin(top, "east0"), EndPin = Pin(bottom, "east1") };
        manager.AddExistingConnection(inner);
        manager.AddExistingConnection(outer);
        manager.RecalculateAllTransmissions();
        return (top, bottom, inner, outer);
    }

    /// <summary>Two flat 1.2 µm straights at the given positions, connected out → in.</summary>
    private static WaveguideConnection RouteFlatStraights(
        double leftX, double leftY, double rightX, double rightY, double processFloor,
        out Component left, out Component right)
    {
        left = TestComponentFactory.CreateFlatStraightWithPhysicalPins("left");
        left.PhysicalX = leftX;
        left.PhysicalY = leftY;
        right = TestComponentFactory.CreateFlatStraightWithPhysicalPins("right");
        right.PhysicalX = rightX;
        right.PhysicalY = rightY;

        var router = new WaveguideRouter { ProcessMinBendRadiusMicrometers = processFloor };
        router.InitializePathfindingGrid(-100, -100, 1100, 900, new[] { left, right });

        var manager = new WaveguideConnectionManager(router);
        var connection = new WaveguideConnection { StartPin = Pin(left, "out"), EndPin = Pin(right, "in") };
        manager.AddExistingConnection(connection);
        manager.RecalculateAllTransmissions();
        return connection;
    }

    private static PhysicalPin Pin(Component component, string name) =>
        component.PhysicalPins.First(p => p.Name == name);
}
