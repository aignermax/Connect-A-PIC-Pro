using CAP_Core.Components;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Routing.MeanderGeneration;
using Shouldly;
using Xunit;

namespace UnitTests.Routing.MeanderGeneration;

/// <summary>
/// <see cref="ConnectionLengthMatcher"/> actuator tests (issue #1008): a live connection
/// gets a target length, the matcher derives the meander request from the design and
/// replaces the route geometry — or passes a typed failure through without touching it.
/// </summary>
public class ConnectionLengthMatcherTests
{
    private const double AssertSlack = 1e-6;
    private const double Tolerance = 1.0;

    [Fact]
    public void ApplyTargetLength_TargetThreeTimesDirect_MeandersWithinTolerance()
    {
        var (comp1, comp2, connection) = CreateStraightDesign();
        var components = new List<Component> { comp1, comp2 };
        double target = 3.0 * connection.PathLengthMicrometers;
        var matcher = new ConnectionLengthMatcher();

        var request = matcher.BuildRequest(connection, components, target, Tolerance);
        var result = matcher.ApplyTargetLength(connection, components, target, Tolerance);

        result.IsSuccess.ShouldBeTrue(result.FailureMessage);
        result.FailureReason.ShouldBeNull();
        connection.PathLengthMicrometers.ShouldBe(target, Tolerance);
        connection.RoutedPath.ShouldBeSameAs(result.Path);
        connection.TargetLengthMicrometers.ShouldBe(target);
        connection.LengthToleranceMicrometers.ShouldBe(Tolerance);
        connection.IsRouteFrozen.ShouldBeTrue(
            "the meandered geometry must survive later recalculations while the endpoints stay put");

        foreach (var bend in connection.RoutedPath!.Segments.OfType<BendSegment>())
        {
            bend.RadiusMicrometers.ShouldBeGreaterThanOrEqualTo(connection.BendRadiusMicrometers);
        }

        foreach (var segment in connection.RoutedPath!.Segments)
        {
            request.Bounds.Contains(PathSegmentBounds.Of(segment), AssertSlack).ShouldBeTrue(
                "every segment must stay inside the derived free-area bounds");
        }
    }

    [Fact]
    public void ApplyTargetLength_MeanderedRoute_KeepsEndpointPoses()
    {
        var (comp1, comp2, connection) = CreateStraightDesign();
        var components = new List<Component> { comp1, comp2 };
        double target = 3.0 * connection.PathLengthMicrometers;
        var (expectedStartX, expectedStartY) = connection.StartPin.GetAbsolutePosition();
        var (expectedEndX, expectedEndY) = connection.EndPin.GetAbsolutePosition();

        var result = new ConnectionLengthMatcher().ApplyTargetLength(
            connection, components, target, Tolerance);

        result.IsSuccess.ShouldBeTrue(result.FailureMessage);
        var path = connection.RoutedPath!;
        path.IsValid.ShouldBeTrue();
        path.Segments[0].StartPoint.X.ShouldBe(expectedStartX, AssertSlack);
        path.Segments[0].StartPoint.Y.ShouldBe(expectedStartY, AssertSlack);
        path.Segments[^1].EndPoint.X.ShouldBe(expectedEndX, AssertSlack);
        path.Segments[^1].EndPoint.Y.ShouldBe(expectedEndY, AssertSlack);
    }

    [Fact]
    public void ApplyTargetLength_TargetShorterThanDirect_TypedFailureLeavesRouteUntouched()
    {
        var (comp1, comp2, connection) = CreateStraightDesign();
        var components = new List<Component> { comp1, comp2 };
        var routeBefore = connection.RoutedPath;
        double target = connection.PathLengthMicrometers / 2.0;

        var result = new ConnectionLengthMatcher().ApplyTargetLength(
            connection, components, target, Tolerance);

        result.IsSuccess.ShouldBeFalse();
        result.FailureReason.ShouldBe(MeanderFailureReason.TargetShorterThanDirectPath);
        result.FailureMessage.ShouldNotBeNullOrEmpty();
        connection.RoutedPath.ShouldBeSameAs(routeBefore);
        connection.PathLengthMicrometers.ShouldBe(routeBefore!.TotalLengthMicrometers, AssertSlack);
        connection.TargetLengthMicrometers.ShouldBeNull();
        connection.LengthToleranceMicrometers.ShouldBeNull();
        connection.IsRouteFrozen.ShouldBeFalse();
    }

    [Fact]
    public void ApplyTargetLength_NoFreeAreaAroundRoute_TypedFailureLeavesRouteUntouched()
    {
        var (comp1, comp2, connection) = CreateStraightDesign();
        // Wall components directly above and below the straight route clamp the
        // perpendicular inflation to zero, so no meander can fit.
        var wallAbove = CreateComponent("wall_above", 90, 0, 170, 24);
        var wallBelow = CreateComponent("wall_below", 90, 26, 170, 24);
        var components = new List<Component> { comp1, comp2, wallAbove, wallBelow };
        var routeBefore = connection.RoutedPath;
        double target = 3.0 * connection.PathLengthMicrometers;

        var result = new ConnectionLengthMatcher().ApplyTargetLength(
            connection, components, target, Tolerance);

        result.IsSuccess.ShouldBeFalse();
        result.FailureReason.ShouldBe(MeanderFailureReason.BoundsTooSmallForMeander);
        connection.RoutedPath.ShouldBeSameAs(routeBefore);
        connection.TargetLengthMicrometers.ShouldBeNull();
    }

    [Fact]
    public void ApplyTargetLength_ProcessFloorAboveConnectionRadius_BendsRespectFloor()
    {
        var (comp1, comp2, connection) = CreateStraightDesign();
        var components = new List<Component> { comp1, comp2 };
        double target = 3.0 * connection.PathLengthMicrometers;
        var router = new WaveguideRouter { ProcessMinBendRadiusMicrometers = 20.0 };

        var result = new ConnectionLengthMatcher(router).ApplyTargetLength(
            connection, components, target, Tolerance);

        result.IsSuccess.ShouldBeTrue(result.FailureMessage);
        connection.PathLengthMicrometers.ShouldBe(target, Tolerance);
        foreach (var bend in connection.RoutedPath!.Segments.OfType<BendSegment>())
        {
            bend.RadiusMicrometers.ShouldBeGreaterThanOrEqualTo(20.0);
        }
    }

    [Fact]
    public void ApplyTargetLength_ReappliedToMeanderedConnection_ReDerivesIdenticalGeometry()
    {
        var (comp1, comp2, connection) = CreateStraightDesign();
        var components = new List<Component> { comp1, comp2 };
        double target = 3.0 * connection.PathLengthMicrometers;
        var matcher = new ConnectionLengthMatcher();

        matcher.ApplyTargetLength(connection, components, target, Tolerance);
        double firstLength = connection.PathLengthMicrometers;
        int firstSegmentCount = connection.RoutedPath!.Segments.Count;

        var result = matcher.ApplyTargetLength(connection, components, target, Tolerance);

        result.IsSuccess.ShouldBeTrue(result.FailureMessage);
        connection.PathLengthMicrometers.ShouldBe(firstLength, AssertSlack);
        connection.RoutedPath!.Segments.Count.ShouldBe(firstSegmentCount);
    }

    /// <summary>
    /// Two facing components (out-pin at angle 0°, in-pin at 180°) joined by a 150 µm
    /// straight route at y = 25.
    /// </summary>
    private static (Component Comp1, Component Comp2, WaveguideConnection Connection) CreateStraightDesign()
    {
        var comp1 = CreateComponent("src", 0, 0, 100, 50);
        var startPin = new PhysicalPin
        {
            Name = "out",
            ParentComponent = comp1,
            OffsetXMicrometers = 100,
            OffsetYMicrometers = 25,
            AngleDegrees = 0
        };
        comp1.PhysicalPins.Add(startPin);

        var comp2 = CreateComponent("dst", 250, 0, 100, 50);
        var endPin = new PhysicalPin
        {
            Name = "in",
            ParentComponent = comp2,
            OffsetXMicrometers = 0,
            OffsetYMicrometers = 25,
            AngleDegrees = 180
        };
        comp2.PhysicalPins.Add(endPin);

        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(100, 25, 250, 25, 0));
        var connection = new WaveguideConnection { StartPin = startPin, EndPin = endPin };
        connection.RestoreCachedPath(path);
        return (comp1, comp2, connection);
    }

    private static Component CreateComponent(
        string identifier, double x, double y, double width, double height)
    {
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin>());

        var component = new Component(
            laserWaveLengthToSMatrixMap: new Dictionary<int, SMatrix>(),
            sliders: new List<Slider>(),
            nazcaFunctionName: $"test_{identifier.ToLower()}",
            nazcaFunctionParams: "",
            parts: parts,
            typeNumber: 0,
            identifier: identifier,
            rotationCounterClock: DiscreteRotation.R0,
            physicalPins: new List<PhysicalPin>());

        component.PhysicalX = x;
        component.PhysicalY = y;
        component.WidthMicrometers = width;
        component.HeightMicrometers = height;
        return component;
    }
}
