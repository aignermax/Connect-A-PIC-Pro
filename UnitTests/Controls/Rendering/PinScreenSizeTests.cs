using CAP.Avalonia.Controls.Rendering;
using Shouldly;
using Xunit;

namespace UnitTests.Controls.Rendering;

/// <summary>
/// Tests for <see cref="PinScreenSize.CapWorldRadius"/>: pins shrink proportionally with the
/// world at low zoom (unchanged from the uncapped formula) and cap at a fixed on-screen size
/// once zooming in would otherwise make them grow to fill the screen.
/// </summary>
public class PinScreenSizeTests
{
    [Theory]
    [InlineData(5.0, 0.5)]
    [InlineData(5.0, 1.0)]
    [InlineData(8.0, 1.0)]
    [InlineData(12.0, 1.0)]
    public void CapWorldRadius_BelowCap_ReturnsWorldSizeUnchanged(double worldSize, double zoom)
    {
        // MaxRadiusPx (16) / zoom exceeds worldSize at these combinations, so the world-space
        // size passes through unchanged — pins still shrink proportionally when zooming out.
        double result = PinScreenSize.CapWorldRadius(worldSize, zoom);

        result.ShouldBe(worldSize);
    }

    [Fact]
    public void CapWorldRadius_AtExtremeZoom_CapsAtMaxRadiusPxDividedByZoom()
    {
        const double zoom = 50.0;
        double result = PinScreenSize.CapWorldRadius(12.0, zoom);

        result.ShouldBe(PinScreenSize.MaxRadiusPx / zoom, 1e-9);
        (result * zoom).ShouldBe(PinScreenSize.MaxRadiusPx, 1e-9,
            "the on-screen size after the zoom transform must never exceed the cap");
    }

    [Fact]
    public void CapWorldRadius_ScalesProportionally_WhenZoomingOutBelowTheCap()
    {
        double atFullZoom = PinScreenSize.CapWorldRadius(5.0, 1.0);
        double atLowZoom = PinScreenSize.CapWorldRadius(5.0, 0.1);

        atLowZoom.ShouldBe(atFullZoom,
            "zooming out below the cap must not change the world-space size (it still shrinks with the world)");
    }

    [Fact]
    public void CapWorldRadius_TreatsNonPositiveZoomAsOne()
    {
        double result = PinScreenSize.CapWorldRadius(5.0, 0.0);

        result.ShouldBe(5.0);
    }

    [Theory]
    [InlineData(10.0, 0.8)]
    [InlineData(10.0, 1.0)]
    [InlineData(12.0, 1.0)]
    public void ClampWorldFontSize_BetweenBounds_ReturnsWorldSizeUnchanged(double worldFontSize, double zoom)
    {
        // worldFontSize * zoom lands strictly between MinLabelFontSizePx (6) and
        // MaxLabelFontSizePx (14) at these combinations, so nothing clamps — labels still
        // shrink/grow proportionally with the world exactly like CapWorldRadius below its cap.
        double result = PinScreenSize.ClampWorldFontSize(worldFontSize, zoom);

        result.ShouldBe(worldFontSize);
    }

    [Fact]
    public void ClampWorldFontSize_AtHighZoom_ClampsAtMaxLabelFontSizePxDividedByZoom()
    {
        const double zoom = 20.0;
        double result = PinScreenSize.ClampWorldFontSize(12.0, zoom);

        result.ShouldBe(PinScreenSize.MaxLabelFontSizePx / zoom, 1e-9);
        (result * zoom).ShouldBe(PinScreenSize.MaxLabelFontSizePx, 1e-9,
            "the on-screen font size after the zoom transform must never exceed the max");
    }

    [Fact]
    public void ClampWorldFontSize_AtLowZoom_ClampsAtMinLabelFontSizePxDividedByZoom()
    {
        // 12 world units * 0.1 zoom would be 1.2 screen px, far below the legible minimum — the
        // clamp floors it instead of letting the label shrink into illegibility (issue: hover
        // and orientation both broke when a hard readability cutoff hid labels entirely here).
        const double zoom = 0.1;
        double result = PinScreenSize.ClampWorldFontSize(12.0, zoom);

        result.ShouldBe(PinScreenSize.MinLabelFontSizePx / zoom, 1e-9);
        (result * zoom).ShouldBe(PinScreenSize.MinLabelFontSizePx, 1e-9,
            "the on-screen font size after the zoom transform must never fall below the min");
    }

    [Fact]
    public void ClampWorldFontSize_NeverProducesAnUnreadableOrInvisibleResult()
    {
        // Across a wide zoom sweep, the clamped on-screen size must always stay within bounds —
        // a label (especially a hovered/selected one) must never vanish at any zoom.
        double[] zooms = { 0.05, 0.1, 0.3, 0.5, 1.0, 2.0, 5.0, 10.0, 50.0 };
        foreach (var zoom in zooms)
        {
            double screenPx = PinScreenSize.ClampWorldFontSize(12.0, zoom) * zoom;
            screenPx.ShouldBeGreaterThanOrEqualTo(PinScreenSize.MinLabelFontSizePx - 1e-9);
            screenPx.ShouldBeLessThanOrEqualTo(PinScreenSize.MaxLabelFontSizePx + 1e-9);
        }
    }

    [Fact]
    public void ClampWorldFontSize_TreatsNonPositiveZoomAsOne()
    {
        double result = PinScreenSize.ClampWorldFontSize(10.0, 0.0);

        result.ShouldBe(10.0);
    }
}
