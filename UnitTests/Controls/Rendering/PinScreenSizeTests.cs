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
}
