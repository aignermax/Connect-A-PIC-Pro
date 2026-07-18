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
/// Visual diagnostic for AUTO routing under a fabrication-process bend-radius floor
/// (e.g. Cornerstone SiN, 30 µm). Renders the REAL design canvas for two layouts that
/// degenerated after the floor started feeding the A* router:
/// (a) two stacked 4-pin couplers with two nested "parallel" connections on the right
///     side (inner pins and outer pins) — the routes must never cross each other;
/// (b) two components whose facing pins sit only a few tens of µm apart — the route
///     must not loop through itself or its own component.
/// One PNG per layout × floor value is written to
/// <c>UI_SHOT_DIR/process-min-radius-routing/{layout}-floor{floor}.png</c>.
/// </summary>
[Trait("Category", "UiScreenshots")]
public class ProcessMinRadiusRoutingDiagnosticTests
{
    private const int CanvasWidth = 1100;
    private const int CanvasHeight = 800;

    /// <summary>Headless renders occasionally miss a frame; retrying the capture makes the PNGs reliable.</summary>
    private const int CaptureAttempts = 3;

    private static readonly double[] Floors = { 10.0, 30.0 };

    /// <summary>Captures both layouts under every process bend-radius floor.</summary>
    [AvaloniaFact]
    public async Task CaptureAutoRoutingUnderProcessFloor()
    {
        var shotRoot = Environment.GetEnvironmentVariable("UI_SHOT_DIR");
        if (string.IsNullOrEmpty(shotRoot))
            return;
        var outputDir = Path.Combine(shotRoot, "process-min-radius-routing");
        Directory.CreateDirectory(outputDir);
        foreach (var stale in Directory.GetFiles(outputDir, "*.png"))
            File.Delete(stale);

        foreach (var floor in Floors)
        {
            await CaptureParallelCouplers(outputDir, floor);
            await CaptureTightNeighbors(outputDir, floor);
        }

        Directory.GetFiles(outputDir, "*.png").Length.ShouldBe(Floors.Length * 2,
            "every layout × floor must yield a PNG");
    }

    /// <summary>
    /// Layout (a): two stacked 4-pin couplers; inner right pins connected to each other and
    /// outer right pins connected to each other — two nested U-turns on the right side.
    /// </summary>
    private static async Task CaptureParallelCouplers(string outputDir, double floor)
    {
        var canvas = new DesignCanvasViewModel();
        canvas.Router.ProcessMinBendRadiusMicrometers = floor;

        var top = TestComponentFactory.CreateFourPinCouplerWithPhysicalPins("top");
        top.PhysicalX = 60;
        top.PhysicalY = 60;
        var bottom = TestComponentFactory.CreateFourPinCouplerWithPhysicalPins("bottom");
        bottom.PhysicalX = 60;
        bottom.PhysicalY = 420;
        canvas.AddComponent(top);
        canvas.AddComponent(bottom);

        await Capture(canvas, outputDir, $"parallel-couplers-floor{floor:F0}", async () =>
        {
            (await canvas.ConnectPinsAsync(Pin(top, "east1"), Pin(bottom, "east0")))
                .ShouldNotBeNull("inner connection must be created");
            (await canvas.ConnectPinsAsync(Pin(top, "east0"), Pin(bottom, "east1")))
                .ShouldNotBeNull("outer connection must be created");
        });
    }

    /// <summary>
    /// Layout (b): two components whose facing pins are only ~40 µm apart in X — with a
    /// 30 µm floor this historically produced full-circle loops through the component.
    /// </summary>
    private static async Task CaptureTightNeighbors(string outputDir, double floor)
    {
        var canvas = new DesignCanvasViewModel();
        canvas.Router.ProcessMinBendRadiusMicrometers = floor;

        var left = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        left.PhysicalX = 60;
        left.PhysicalY = 60;
        var right = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        right.PhysicalX = 350;
        right.PhysicalY = 100;
        canvas.AddComponent(left);
        canvas.AddComponent(right);

        await Capture(canvas, outputDir, $"tight-neighbors-floor{floor:F0}", async () =>
        {
            (await canvas.ConnectPinsAsync(Pin(left, "out"), Pin(right, "in")))
                .ShouldNotBeNull("tight connection must be created");
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
                designCanvas.InvalidateVisual();
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
