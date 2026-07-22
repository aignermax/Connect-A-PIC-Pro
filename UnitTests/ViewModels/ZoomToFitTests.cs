using CAP.Avalonia.ViewModels;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.ViewModels;

/// <summary>
/// Tests for <see cref="MainViewModel.ZoomToFit"/> and viewport centering.
/// </summary>
[Trait("Category", "Slow")]
public class ZoomToFitTests
{
    private static MainViewModel CreateViewModel() =>
        MainViewModelTestHelper.CreateMainViewModel();

    [Fact]
    public void ZoomToFit_EmptyCanvas_DoesNotChangeZoom()
    {
        var vm = CreateViewModel();
        double originalZoom = vm.ViewportControl.ZoomLevel;

        vm.ZoomToFit(800, 600);

        vm.ViewportControl.ZoomLevel.ShouldBe(originalZoom);
        vm.StatusText.ShouldContain("No components");
    }

    [Fact]
    public void ZoomToFit_WithComponents_SetsZoomAndPan()
    {
        var vm = CreateViewModel();
        var comp = TestComponentFactory.CreateStraightWaveGuide();
        comp.PhysicalX = 100;
        comp.PhysicalY = 100;
        comp.WidthMicrometers = 200;
        comp.HeightMicrometers = 200;
        vm.Canvas.AddComponent(comp);

        vm.ZoomToFit(800, 600);

        vm.ViewportControl.ZoomLevel.ShouldBeGreaterThan(0);
        vm.StatusText.ShouldContain("Zoom to fit");
    }

    [Fact]
    public void ZoomToFit_ZeroViewport_DoesNothing()
    {
        var vm = CreateViewModel();
        double originalZoom = vm.ViewportControl.ZoomLevel;

        vm.ZoomToFit(0, 0);

        vm.ViewportControl.ZoomLevel.ShouldBe(originalZoom);
    }

    [Fact]
    public void ZoomToFit_NegativeViewport_DoesNothing()
    {
        var vm = CreateViewModel();
        double originalZoom = vm.ViewportControl.ZoomLevel;

        vm.ZoomToFit(-100, -100);

        vm.ViewportControl.ZoomLevel.ShouldBe(originalZoom);
    }

    [Fact]
    public void ZoomToFit_MultipleComponents_FitsAll()
    {
        var vm = CreateViewModel();

        var comp1 = TestComponentFactory.CreateStraightWaveGuide();
        comp1.PhysicalX = 0;
        comp1.PhysicalY = 0;
        comp1.WidthMicrometers = 100;
        comp1.HeightMicrometers = 100;
        vm.Canvas.AddComponent(comp1);

        var comp2 = TestComponentFactory.CreateStraightWaveGuide();
        comp2.PhysicalX = 900;
        comp2.PhysicalY = 900;
        comp2.WidthMicrometers = 100;
        comp2.HeightMicrometers = 100;
        vm.Canvas.AddComponent(comp2);

        vm.ZoomToFit(1000, 1000);

        // Bounding box: (0,0)-(1000,1000), with 10% padding: (-100,-100)-(1100,1100)
        // padded size: 1200x1200, viewport: 1000x1000
        // zoom = 1000/1200 ≈ 0.833
        vm.ViewportControl.ZoomLevel.ShouldBeInRange(0.8, 0.9);
    }

    [Fact]
    public void ZoomToFit_ContentCenteredInViewport_PanCorrect()
    {
        // Verifies that ZoomToFit centers content at (vpWidth/2, vpHeight/2).
        // This test guards against the bug where window ClientSize (including sidebars)
        // was used instead of the canvas control bounds, causing wrong pan centering.
        var vm = CreateViewModel();

        var comp = TestComponentFactory.CreateStraightWaveGuide();
        comp.PhysicalX = 0;
        comp.PhysicalY = 0;
        comp.WidthMicrometers = 200;
        comp.HeightMicrometers = 200;
        vm.Canvas.AddComponent(comp);

        const double vpWidth = 800;
        const double vpHeight = 600;
        vm.ZoomToFit(vpWidth, vpHeight);

        // After ZoomToFit, the bounding box center should appear at the viewport center.
        // boxCenter = (100, 100), with 10% padding: padded = (-20,-20)-(220,220)
        // paddedCenter = (100, 100), zoom = min(800/240, 600/240) = min(3.33, 2.5) = 2.5
        // panX = 800/2 - 100 * 2.5 = 400 - 250 = 150
        // panY = 600/2 - 100 * 2.5 = 300 - 250 = 50
        // Verify: boxCenter * zoom + pan = vpCenter
        double centerX = 100;
        double centerY = 100;
        var zoom = vm.ViewportControl.ZoomLevel;
        var panX = vm.Canvas.PanX;
        var panY = vm.Canvas.PanY;

        (centerX * zoom + panX).ShouldBeInRange(vpWidth / 2 - 1, vpWidth / 2 + 1);
        (centerY * zoom + panY).ShouldBeInRange(vpHeight / 2 - 1, vpHeight / 2 + 1);
    }

    [Fact]
    public void ZoomToFit_WithIncorrectlyLargerViewport_ProducesDifferentPan()
    {
        // Documents that using window width (e.g. 1400) instead of canvas width (e.g. 800)
        // produces a different (wrong) pan. This is the root cause of the initialization bug.
        var vm1 = CreateViewModel();
        var vm2 = CreateViewModel();

        var comp1 = TestComponentFactory.CreateStraightWaveGuide();
        comp1.PhysicalX = 100;
        comp1.PhysicalY = 100;
        comp1.WidthMicrometers = 200;
        comp1.HeightMicrometers = 200;
        vm1.Canvas.AddComponent(comp1);

        var comp2 = TestComponentFactory.CreateStraightWaveGuide();
        comp2.PhysicalX = 100;
        comp2.PhysicalY = 100;
        comp2.WidthMicrometers = 200;
        comp2.HeightMicrometers = 200;
        vm2.Canvas.AddComponent(comp2);

        // Correct: use canvas viewport size
        vm1.ZoomToFit(800, 600);
        // Wrong: use full window size (includes 300px left panel + 300px right panel)
        vm2.ZoomToFit(1400, 600);

        // PanX differs because the center used in calculation differs
        vm1.Canvas.PanX.ShouldNotBe(vm2.Canvas.PanX);
        // PanY is the same because height is identical
        vm1.Canvas.PanY.ShouldBe(vm2.Canvas.PanY, tolerance: 0.01);
    }

    [Fact]
    public void ViewportControl_GetViewportSize_UsedInNavigateCanvasTo()
    {
        // Verifies that NavigateCanvasTo uses the GetViewportSize callback,
        // meaning fixing GetActualViewportSize in the view also fixes navigation.
        var vm = CreateViewModel();
        const double canvasWidth = 800.0;
        const double canvasHeight = 600.0;
        vm.ViewportControl.GetViewportSize = () => (canvasWidth, canvasHeight);
        vm.ViewportControl.ZoomLevel = 1.0;

        vm.ViewportControl.NavigateCanvasTo(centerX: 100, centerY: 150);

        // panX = vpWidth/2 - centerX * zoom = 400 - 100 = 300
        vm.Canvas.PanX.ShouldBe(300, tolerance: 0.01);
        // panY = vpHeight/2 - centerY * zoom = 300 - 150 = 150
        vm.Canvas.PanY.ShouldBe(150, tolerance: 0.01);
    }
}
