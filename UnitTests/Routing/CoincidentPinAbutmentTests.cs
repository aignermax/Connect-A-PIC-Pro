using CAP_Core.Analysis;
using CAP_Core.Components;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Components.FormulaReading;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Routing;

/// <summary>
/// A connection whose two pins sit at the same point (below the router's 1 µm
/// endpoint tolerance) is a perfect abutment — two cells touching pin-to-pin.
/// The router must return a minimal butt joint for it, not a degenerate CSC
/// fallback flagged <see cref="RoutedPath.IsBlockedFallback"/>, and the design
/// checks must not report <see cref="DesignIssueType.BlockedPath"/>.
/// </summary>
public class CoincidentPinAbutmentTests
{
    private const double BendRadius = 10.0;
    private const double GeometryToleranceMicrometers = 1e-6;

    [Fact]
    public void Route_ExactlyCoincidentOpposingPins_NoGrid_ReturnsUnblockedAbutment()
    {
        var (startPin, endPin, router) = CreateAbutmentPair(pinSeparation: 0, useGrid: false);

        var path = router.Route(startPin, endPin);

        AssertValidAbutment(path, startPin, endPin);
    }

    [Fact]
    public void Route_ExactlyCoincidentOpposingPins_WithGrid_ReturnsUnblockedAbutment()
    {
        var (startPin, endPin, router) = CreateAbutmentPair(pinSeparation: 0, useGrid: true);

        var path = router.Route(startPin, endPin);

        AssertValidAbutment(path, startPin, endPin);
    }

    [Fact]
    public void Route_NearlyCoincidentPins_BelowEndpointTolerance_ReturnsUnblockedAbutment()
    {
        var (startPin, endPin, router) = CreateAbutmentPair(pinSeparation: 0.5, useGrid: true);

        var path = router.Route(startPin, endPin);

        AssertValidAbutment(path, startPin, endPin);
    }

    [Fact]
    public void Route_CoincidentPinsSameDirection_ReturnsUnblockedAbutment()
    {
        var (startPin, endPin, router) = CreateAbutmentPair(pinSeparation: 0, useGrid: true, endPinAngle: 0);

        var path = router.Route(startPin, endPin);

        AssertValidAbutment(path, startPin, endPin);
    }

    [Fact]
    public void Route_PinsAboveEndpointTolerance_StillRoutedNormally()
    {
        var startComponent = CreateComponent(0, 0);
        var endComponent = CreateComponent(40, 0);
        var startPin = CreatePin(startComponent, 10, 5, 0);
        var endPin = CreatePin(endComponent, 0, 5, 180);
        var router = CreateRouter(startComponent, endComponent);

        var path = router.Route(startPin, endPin);

        path.IsBlockedFallback.ShouldBeFalse();
        path.IsValid.ShouldBeTrue();
        path.TotalLengthMicrometers.ShouldBeGreaterThan(1.0);
    }

    [Fact]
    public void DesignValidator_CoincidentPinConnection_ReportsNoIssues()
    {
        var (startPin, endPin, router) = CreateAbutmentPair(pinSeparation: 0, useGrid: true);
        var connection = new WaveguideConnection { StartPin = startPin, EndPin = endPin };

        connection.RecalculateTransmission(router);
        var issues = new DesignValidator().Validate(new[] { connection });

        connection.IsBlockedFallback.ShouldBeFalse();
        issues.ShouldBeEmpty();
    }

    private static void AssertValidAbutment(RoutedPath path, PhysicalPin startPin, PhysicalPin endPin)
    {
        var (startX, startY) = startPin.GetAbsolutePosition();
        var (endX, endY) = endPin.GetAbsolutePosition();

        path.IsBlockedFallback.ShouldBeFalse();
        path.IsPlaceholderGeometry.ShouldBeFalse();
        path.IsInvalidGeometry.ShouldBeFalse();
        path.IsValid.ShouldBeTrue();
        path.Segments.Count.ShouldBe(1);
        var straight = path.Segments[0].ShouldBeOfType<StraightSegment>();
        straight.StartPoint.X.ShouldBe(startX, GeometryToleranceMicrometers);
        straight.StartPoint.Y.ShouldBe(startY, GeometryToleranceMicrometers);
        straight.EndPoint.X.ShouldBe(endX, GeometryToleranceMicrometers);
        straight.EndPoint.Y.ShouldBe(endY, GeometryToleranceMicrometers);
    }

    /// <summary>
    /// Creates two 10×10 µm components abutting side by side so the start pin
    /// (east edge of the first) and the end pin (west edge of the second) sit
    /// <paramref name="pinSeparation"/> µm apart, facing each other.
    /// </summary>
    private static (PhysicalPin start, PhysicalPin end, WaveguideRouter router) CreateAbutmentPair(
        double pinSeparation, bool useGrid, double endPinAngle = 180)
    {
        var startComponent = CreateComponent(0, 0);
        var endComponent = CreateComponent(10 + pinSeparation, 0);
        var startPin = CreatePin(startComponent, 10, 5, 0);
        var endPin = CreatePin(endComponent, 0, 5, endPinAngle);
        var router = useGrid
            ? CreateRouter(startComponent, endComponent)
            : new WaveguideRouter { MinBendRadiusMicrometers = BendRadius };
        return (startPin, endPin, router);
    }

    private static WaveguideRouter CreateRouter(params Component[] components)
    {
        var router = new WaveguideRouter
        {
            MinBendRadiusMicrometers = BendRadius,
            MinWaveguideSpacingMicrometers = 2.0,
        };
        router.InitializePathfindingGrid(-50, -50, 200, 200, components);
        return router;
    }

    private static PhysicalPin CreatePin(Component component, double offsetX, double offsetY, double angle) =>
        new()
        {
            Name = angle < 90 || angle > 270 ? "output" : "input",
            OffsetXMicrometers = offsetX,
            OffsetYMicrometers = offsetY,
            AngleDegrees = angle,
            ParentComponent = component,
        };

    private static Component CreateComponent(double x, double y)
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
            identifier: $"AbutmentCell_{x}_{y}",
            rotationCounterClock: DiscreteRotation.R0)
        {
            WidthMicrometers = 10,
            HeightMicrometers = 10,
            PhysicalX = x,
            PhysicalY = y,
        };
    }
}
