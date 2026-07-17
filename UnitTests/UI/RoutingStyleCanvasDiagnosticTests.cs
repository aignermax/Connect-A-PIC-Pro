using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CAP.Avalonia.Controls;
using CAP.Avalonia.Views;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.Components.Connections;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Visual diagnostic for the per-connection routing styles: renders the REAL design canvas
/// once per <see cref="WaveguideType"/> in two everyday layouts (parallel pins with a lateral
/// offset, and axially aligned pins) and writes one PNG each to
/// <c>UI_SHOT_DIR/routing-styles/{layout}-{style}.png</c>. The images are the ground truth
/// for judging that SBend (sine), Cobra (Hermite), Bend/Euler (generous arcs) and Straight
/// draw visibly distinct, smooth curves that connect BOTH pins.
/// </summary>
[Trait("Category", "UiScreenshots")]
public class RoutingStyleCanvasDiagnosticTests
{
    private const int CanvasWidth = 900;
    private const int CanvasHeight = 520;

    /// <summary>Headless renders occasionally miss a frame; re-pumping the dispatcher and
    /// retrying the capture up to this many times makes all 12 PNGs come out reliably.</summary>
    private const int CaptureAttempts = 3;

    private static readonly WaveguideType[] Styles =
    {
        WaveguideType.Auto, WaveguideType.Straight, WaveguideType.Bend,
        WaveguideType.SBend, WaveguideType.Euler, WaveguideType.Cobra,
    };

    /// <summary>Captures all styles in the offset and the aligned layout (12 PNGs).</summary>
    [AvaloniaFact]
    public async Task CaptureAllRoutingStylesOnTheCanvas()
    {
        // Opt-in like UiScreenshotTests: only runs when screenshots are explicitly requested.
        var shotRoot = Environment.GetEnvironmentVariable("UI_SHOT_DIR");
        if (string.IsNullOrEmpty(shotRoot))
            return;
        var outputDir = Path.Combine(shotRoot, "routing-styles");
        Directory.CreateDirectory(outputDir);
        foreach (var stale in Directory.GetFiles(outputDir, "*.png"))
            File.Delete(stale);

        await CaptureLayout(outputDir, "offset", endOffsetY: 80);
        await CaptureLayout(outputDir, "aligned", endOffsetY: 0);

        Directory.GetFiles(outputDir, "*.png").Length.ShouldBe(Styles.Length * 2,
            "every style × layout must yield a PNG");
    }

    /// <summary>Builds one two-component design, then re-styles its single connection once
    /// per <see cref="Styles"/> entry and captures the canvas after each restyle.</summary>
    private static async Task CaptureLayout(string outputDir, string layoutName, double endOffsetY)
    {
        var canvas = new DesignCanvasViewModel();
        var startComp = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        startComp.PhysicalX = 40;
        startComp.PhysicalY = 40;
        var endComp = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        endComp.PhysicalX = 490;
        endComp.PhysicalY = 40 + endOffsetY;
        canvas.AddComponent(startComp);
        canvas.AddComponent(endComp);

        var startPin = startComp.PhysicalPins.First(p => p.Name == "out");
        var endPin = endComp.PhysicalPins.First(p => p.Name == "in");
        var connVm = await canvas.ConnectPinsAsync(startPin, endPin);
        connVm.ShouldNotBeNull($"pins must connect in the {layoutName} layout");

        var vm = MainViewModelTestHelper.CreateMainViewModel(canvas: canvas);
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
            foreach (var style in Styles)
            {
                connVm!.Connection.Type = style;
                connVm.Connection.IsRouteFrozen = false;
                connVm.Connection.InvalidateRoute();
                await canvas.RecalculateRoutesAsync();

                // Drain any repaint jobs the recalculation posted (they may still show the
                // transient invalidated route), then force a fresh render of the final path.
                Dispatcher.UIThread.RunJobs();
                foreach (var designCanvas in window.GetVisualDescendants().OfType<DesignCanvas>())
                    designCanvas.InvalidateVisual();
                Dispatcher.UIThread.RunJobs();

                var path = Path.Combine(outputDir, $"{layoutName}-{style}.png");
                CaptureWithRetry(window, path);
            }
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>
    /// Captures the window to <paramref name="path"/>. The headless renderer is flaky in two
    /// ways: <c>CaptureRenderedFrame</c> can return null (frame miss) or a STALE frame rendered
    /// mid-recalculation (e.g. the direct-line placeholder drawn while the route was
    /// invalidated). Pumping the dispatcher before EVERY attempt and keeping the LAST
    /// successful capture guarantees the freshest fully-rendered frame is written.
    /// </summary>
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
