using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Routing;

/// <summary>
/// Round-5 review [2]: a cached design-file route must be validated against the
/// CURRENT pin angles — a PDK calibration fix (DC Halfring 90°→270°) leaves pin
/// positions untouched, so only the docking-direction check can detect that the
/// saved geometry now runs against the port.
/// </summary>
public class CachedRouteValidatorTests
{
    [Fact]
    public void MatchingDockingDirections_ReportMatch()
    {
        var (startPin, endPin) = CreatePinPair(startAngle: 0, endAngle: 180);
        var path = StraightPathBetween(startPin, endPin);

        CachedRouteValidator.CheckPinDirections(startPin, endPin, path)
            .ShouldBe((true, true));
    }

    [Fact]
    public void FlippedPinAngles_ReportMismatch_OnBothEnds()
    {
        // The calibration scenario: geometry saved for 0°/180° pins, but the PDK now
        // declares the pins flipped by 180° (like the Halfring 90°→270° correction).
        var (startPin, endPin) = CreatePinPair(startAngle: 180, endAngle: 0);
        var path = StraightPathBetween(startPin, endPin);

        CachedRouteValidator.CheckPinDirections(startPin, endPin, path)
            .ShouldBe((false, false));
    }

    [Fact]
    public void QuantizationSlack_UpTo45Degrees_StillMatches()
    {
        // A* quantizes the launch direction to 45° steps — a 45°-off straight is
        // legitimate routing slack, not a calibration change.
        var (startPin, endPin) = CreatePinPair(startAngle: 45, endAngle: 225);
        var path = StraightPathBetween(startPin, endPin); // runs at 0°

        CachedRouteValidator.CheckPinDirections(startPin, endPin, path)
            .ShouldBe((true, true));
    }

    [Fact]
    public void CollapsedPinLead_DegenerateFirstSegment_StillDetectsFlippedStartPin()
    {
        // A collapsed pin lead leaves a zero-length straight on the pin; its geometric direction
        // is undefined. The check must look past it to the real launch bend, or the pin-direction
        // safety net passes vacuously for every collapsed route.
        var (startPin, endPin) = CreatePinPair(startAngle: 180, endAngle: 0);
        var (sx, sy) = startPin.GetAbsolutePosition();
        var (ex, ey) = endPin.GetAbsolutePosition();

        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(sx, sy, sx, sy, 0));     // degenerate collapsed lead
        path.Segments.Add(new BendSegment(sx, sy + 10, 10, 0, 90));    // launches east (0°), against the 180° pin
        path.Segments.Add(new StraightSegment(sx + 10, sy + 10, ex, ey, 0));

        var (startMatches, _) = CachedRouteValidator.CheckPinDirections(startPin, endPin, path);
        startMatches.ShouldBeFalse("a degenerate collapsed lead must not hide the pin-direction mismatch");
    }

    [Fact]
    public void BlockedFallbackPaths_AreExemptFromTheCheck()
    {
        var (startPin, endPin) = CreatePinPair(startAngle: 180, endAngle: 0);
        var path = StraightPathBetween(startPin, endPin);
        path.IsBlockedFallback = true;

        CachedRouteValidator.CheckPinDirections(startPin, endPin, path)
            .ShouldBe((true, true));
    }

    [Fact]
    public void EmptyPaths_AreExemptFromTheCheck()
    {
        var (startPin, endPin) = CreatePinPair(startAngle: 180, endAngle: 0);

        CachedRouteValidator.CheckPinDirections(startPin, endPin, new RoutedPath())
            .ShouldBe((true, true));
    }

    /// <summary>Two facing components 200 µm apart, pins at the same height.</summary>
    private static (PhysicalPin Start, PhysicalPin End) CreatePinPair(
        double startAngle, double endAngle)
    {
        var comp1 = CreateComponent("C1", x: 100, pinOffsetX: 50, pinAngle: startAngle);
        var comp2 = CreateComponent("C2", x: 300, pinOffsetX: 0, pinAngle: endAngle);
        return (comp1.PhysicalPins[0], comp2.PhysicalPins[0]);
    }

    /// <summary>A single straight segment from the start pin to the end pin (runs at 0°).</summary>
    private static RoutedPath StraightPathBetween(PhysicalPin startPin, PhysicalPin endPin)
    {
        var (sx, sy) = startPin.GetAbsolutePosition();
        var (ex, ey) = endPin.GetAbsolutePosition();
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(sx, sy, ex, ey, 0));
        return path;
    }

    private static Component CreateComponent(
        string identifier, double x, double pinOffsetX, double pinAngle)
    {
        var sMatrix = new SMatrix(new List<Guid>(), new List<(Guid sliderID, double value)>());
        var pins = new List<PhysicalPin>
        {
            new()
            {
                Name = "Pin1",
                OffsetXMicrometers = pinOffsetX,
                OffsetYMicrometers = 15,
                AngleDegrees = pinAngle
            }
        };

        return new Component(
            new Dictionary<int, SMatrix> { { 1550, sMatrix } },
            new List<Slider>(),
            "test",
            "",
            new Part[1, 1] { { new Part() } },
            -1,
            identifier,
            new DiscreteRotation(),
            pins)
        {
            PhysicalX = x,
            PhysicalY = 100,
            WidthMicrometers = 50,
            HeightMicrometers = 30
        };
    }
}
