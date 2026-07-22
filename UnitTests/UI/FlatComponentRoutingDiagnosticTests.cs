using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CAP.Avalonia.Controls;
using CAP.Avalonia.Views;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Core;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Visual diagnostic for AUTO routing with realistically FLAT components under a process
/// bend-radius floor (Cornerstone SiN). Real PDK footprints: "Coupler Straight" is
/// 20 × 2.636 µm with a 1.436 µm pin pitch on each side; "Straight" is 10 × 1.2 µm.
/// Renders the REAL design canvas for (a) two stacked flat couplers with nested parallel
/// connections on the right side at wide/medium/tight vertical gaps, and (b) two flat
/// straights placed close together. One PNG per layout × floor is written to
/// <c>UI_SHOT_DIR/flat-component-routing/{layout}-floor{floor}.png</c>.
/// </summary>
[Trait("Category", "UiScreenshots")]
public class FlatComponentRoutingDiagnosticTests
{
    private const int CanvasWidth = 1100;
    private const int CanvasHeight = 800;

    /// <summary>Magnification so 2.6 µm tall components stay recognizable in the PNG.</summary>
    private const double RenderZoom = 2.2;

    /// <summary>Headless renders occasionally miss a frame; retrying makes the PNGs reliable.</summary>
    private const int CaptureAttempts = 3;

    private static readonly double[] Floors = { 10.0, 30.0 };
    private static readonly double[] CouplerGaps = { 200.0, 80.0, 30.0 };

    /// <summary>Captures every flat layout under every process bend-radius floor.</summary>
    [AvaloniaFact]
    public async Task CaptureFlatComponentRoutingUnderProcessFloor()
    {
        var shotRoot = Environment.GetEnvironmentVariable("UI_SHOT_DIR");
        if (string.IsNullOrEmpty(shotRoot))
            return;
        var outputDir = Path.Combine(shotRoot, "flat-component-routing");
        Directory.CreateDirectory(outputDir);
        foreach (var stale in Directory.GetFiles(outputDir, "*.png"))
            File.Delete(stale);

        foreach (var floor in Floors)
        {
            foreach (var gap in CouplerGaps)
                await CaptureFlatParallelCouplers(outputDir, gap, floor);
            await CaptureFlatStraights(outputDir, floor);
        }

        Directory.GetFiles(outputDir, "*.png").Length.ShouldBe(
            Floors.Length * (CouplerGaps.Length + 1),
            "every layout × floor must yield a PNG");
    }

    /// <summary>
    /// Layout (a): two stacked flat couplers (20 × 2.636 µm, 1.436 µm pin pitch) with nested
    /// connections on the right: top lower pin ↔ bottom upper pin and top upper ↔ bottom lower.
    /// </summary>
    private static async Task CaptureFlatParallelCouplers(string outputDir, double gap, double floor)
    {
        var canvas = new DesignCanvasViewModel();
        canvas.Router.ProcessMinBendRadiusMicrometers = floor;

        var top = TestComponentFactory.CreateFlatCouplerWithPhysicalPins("top");
        top.PhysicalX = 60;
        top.PhysicalY = 60;
        var bottom = TestComponentFactory.CreateFlatCouplerWithPhysicalPins("bottom");
        bottom.PhysicalX = 60;
        bottom.PhysicalY = top.PhysicalY + top.HeightMicrometers + gap;
        canvas.AddComponent(top);
        canvas.AddComponent(bottom);

        await Capture(canvas, outputDir, $"flat-couplers-gap{gap:F0}-floor{floor:F0}", async () =>
        {
            (await canvas.ConnectPinsAsync(Pin(top, "east1"), Pin(bottom, "east0")))
                .ShouldNotBeNull("inner connection must be created");
            (await canvas.ConnectPinsAsync(Pin(top, "east0"), Pin(bottom, "east1")))
                .ShouldNotBeNull("outer connection must be created");
        });
    }

    /// <summary>
    /// Layout (b): two flat 1.2 µm straights placed with a small diagonal offset — the pins
    /// face each other only a few tens of µm apart.
    /// </summary>
    private static async Task CaptureFlatStraights(string outputDir, double floor)
    {
        var canvas = new DesignCanvasViewModel();
        canvas.Router.ProcessMinBendRadiusMicrometers = floor;

        var left = TestComponentFactory.CreateFlatStraightWithPhysicalPins("left");
        left.PhysicalX = 60;
        left.PhysicalY = 60;
        var right = TestComponentFactory.CreateFlatStraightWithPhysicalPins("right");
        right.PhysicalX = 150;
        right.PhysicalY = 100;
        canvas.AddComponent(left);
        canvas.AddComponent(right);

        await Capture(canvas, outputDir, $"flat-straights-floor{floor:F0}", async () =>
        {
            (await canvas.ConnectPinsAsync(Pin(left, "out"), Pin(right, "in")))
                .ShouldNotBeNull("flat straight connection must be created");
        });
    }

    /// <summary>Connects the pins, pumps the dispatcher, and captures the canvas to a PNG.</summary>
    private static async Task Capture(
        DesignCanvasViewModel canvas, string outputDir, string name, Func<Task> connect)
    {
        var vm = MainViewModelTestHelper.CreateMainViewModel(canvas: canvas);
        // MainViewModel rewires the floor provider to the process resolver; this test
        // dictates the floor explicitly, so re-pin the provider to the test value.
        double floor = canvas.Router.ProcessMinBendRadiusMicrometers;
        canvas.Routing.GetProcessMinBendRadiusMicrometers = () => floor;
        var window = new Window
        {
            Width = CanvasWidth,
            Height = CanvasHeight,
            Content = new MainView { DataContext = vm },
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            await connect();
            await canvas.RecalculateRoutesAsync();

            Dispatcher.UIThread.RunJobs();
            foreach (var designCanvas in window.GetVisualDescendants().OfType<DesignCanvas>())
            {
                designCanvas.Zoom = RenderZoom;
                designCanvas.InvalidateVisual();
            }
            Dispatcher.UIThread.RunJobs();

            CaptureWithRetry(window, Path.Combine(outputDir, $"{name}.png"));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static PhysicalPin Pin(Component component, string name) =>
        component.PhysicalPins.First(p => p.Name == name);

    /// <summary>Captures the window with dispatcher pumping between attempts (headless flake guard).</summary>
    private static void CaptureWithRetry(Window window, string path)
    {
        WriteableBitmap? bitmap = null;
        for (int attempt = 0; attempt < CaptureAttempts; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            var frame = window.CaptureRenderedFrame();
            if (frame == null)
                continue;
            bitmap?.Dispose();
            bitmap = frame;
        }

        bitmap.ShouldNotBeNull($"CaptureRenderedFrame stayed null after {CaptureAttempts} attempts for {path}");
        using (bitmap)
        {
            ScreenshotArtifacts.SavePng(bitmap, path);
        }
    }
}
