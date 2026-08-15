using CAP_Core.Analysis;
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
/// Regression tests for issue #812: a connection whose pins sit at the same point
/// (pin distance below <see cref="WaveguideRouter.AbutmentThresholdMicrometers"/> — a
/// gdsfactory-style touching-cell abutment or two components snapped pin-to-pin on the
/// canvas) is geometrically perfect and needs no waveguide. The router must answer with
/// a minimal butt joint, not with the degenerate CSC fallback that used to be flagged
/// <see cref="RoutedPath.IsBlockedFallback"/> and surfaced as a false BlockedPath issue.
/// </summary>
public class CoincidentPinRoutingTests
{
    private const double ComponentSize = 50.0;

    [Fact]
    public void Route_ExactlyCoincidentOpposingPins_ReturnsUnblockedButtJoint()
    {
        var (startPin, endPin, router) = CreateAbutmentPair(separationMicrometers: 0.0);

        var path = router.Route(startPin, endPin);

        path.IsBlockedFallback.ShouldBeFalse("a perfect abutment is valid, not blocked");
        path.IsPlaceholderGeometry.ShouldBeFalse();
        path.IsInvalidGeometry.ShouldBeFalse();
        path.IsValid.ShouldBeTrue();
        path.Segments.Count.ShouldBe(1);
        var straight = path.Segments[0].ShouldBeOfType<StraightSegment>();
        straight.LengthMicrometers.ShouldBe(0.0, 1e-9);
    }

    [Fact]
    public void Route_NearlyCoincidentPins_ReturnsPinToPinStraight()
    {
        const double separation = 0.5;
        var (startPin, endPin, router) = CreateAbutmentPair(separation);

        var path = router.Route(startPin, endPin);

        path.IsBlockedFallback.ShouldBeFalse();
        path.IsValid.ShouldBeTrue();
        var straight = path.Segments.ShouldHaveSingleItem().ShouldBeOfType<StraightSegment>();
        straight.LengthMicrometers.ShouldBe(separation, 1e-9);
    }

    [Fact]
    public void Route_CoincidentPins_WithInitializedGrid_NotFlaggedBlocked()
    {
        // The live-canvas scenario: both component bodies are registered as obstacles and
        // the pins coincide at the shared boundary, inside blocked component cells.
        var (startPin, endPin, router) = CreateAbutmentPair(
            separationMicrometers: 0.0, initializeGrid: true);

        var path = router.Route(startPin, endPin);

        path.IsBlockedFallback.ShouldBeFalse();
        path.IsValid.ShouldBeTrue();
        path.Segments.ShouldHaveSingleItem();
    }

    [Fact]
    public void Route_CoincidentPins_SameOrientation_NotFlaggedBlocked()
    {
        // Pathological layout (both pins face the same way at the same point): there is
        // still nothing to route — a zero-length connection must not read as blocked.
        var (startPin, endPin, router) = CreateAbutmentPair(
            separationMicrometers: 0.0, endPinAngleDegrees: 0.0);

        var path = router.Route(startPin, endPin);

        path.IsBlockedFallback.ShouldBeFalse();
        path.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Route_PinsAtAbutmentThreshold_RoutesNormally()
    {
        // Boundary: at exactly the threshold the connection is NOT an abutment (the import
        // pipeline uses the same exclusive bound) and gets a real routed straight.
        var (startPin, endPin, router) = CreateAbutmentPair(
            WaveguideRouter.AbutmentThresholdMicrometers);

        var path = router.Route(startPin, endPin);

        path.IsBlockedFallback.ShouldBeFalse();
        path.TotalLengthMicrometers.ShouldBeGreaterThanOrEqualTo(
            WaveguideRouter.AbutmentThresholdMicrometers);
    }

    [Fact]
    public void DesignValidator_CoincidentPinConnection_ReportsNoBlockedPath()
    {
        var (startPin, endPin, router) = CreateAbutmentPair(
            separationMicrometers: 0.0, initializeGrid: true);
        var connection = new WaveguideConnection { StartPin = startPin, EndPin = endPin };

        connection.RecalculateTransmission(router);
        var issues = new DesignValidator().Validate(new[] { connection });

        connection.IsBlockedFallback.ShouldBeFalse();
        issues.ShouldBeEmpty();
    }

    /// <summary>
    /// Two 50×50 µm components side by side; the start pin sits on the right edge of the
    /// left component facing east, the end pin <paramref name="separationMicrometers"/>
    /// further east facing west — coincident (a perfect abutment) at separation 0.
    /// </summary>
    private static (PhysicalPin StartPin, PhysicalPin EndPin, WaveguideRouter Router) CreateAbutmentPair(
        double separationMicrometers,
        double endPinAngleDegrees = 180.0,
        bool initializeGrid = false)
    {
        var startComponent = CreateComponent(x: 0, y: 0);
        var endComponent = CreateComponent(x: ComponentSize + separationMicrometers, y: 0);

        var startPin = new PhysicalPin
        {
            Name = "output",
            OffsetXMicrometers = ComponentSize,
            OffsetYMicrometers = ComponentSize / 2,
            AngleDegrees = 0,
            ParentComponent = startComponent
        };
        var endPin = new PhysicalPin
        {
            Name = "input",
            OffsetXMicrometers = 0,
            OffsetYMicrometers = ComponentSize / 2,
            AngleDegrees = endPinAngleDegrees,
            ParentComponent = endComponent
        };

        var router = new WaveguideRouter { MinBendRadiusMicrometers = 10.0 };
        if (initializeGrid)
        {
            router.InitializePathfindingGrid(-100, -100, 400, 250, new[] { startComponent, endComponent });
        }
        return (startPin, endPin, router);
    }

    private static Component CreateComponent(double x, double y)
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
            identifier: $"AbutmentComponent_{x}_{y}",
            rotationCounterClock: DiscreteRotation.R0)
        {
            WidthMicrometers = ComponentSize,
            HeightMicrometers = ComponentSize,
            PhysicalX = x,
            PhysicalY = y
        };
        return component;
    }
}
