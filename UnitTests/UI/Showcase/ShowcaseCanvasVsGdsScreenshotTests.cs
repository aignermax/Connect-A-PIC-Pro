using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CAP.Avalonia.Services.GdsFactoryExport;
using Shouldly;
using UnitTests.UI.Flows;
using Xunit;

namespace UnitTests.UI.Showcase;

/// <summary>
/// v0.12 feature-showcase: "what you draw is what you tape". Composes the staged chip's
/// MZI core as drawn on the canvas next to the rendered geometry of the REAL exported
/// GDS of the same circuit: the gdsfactory script produced by the production exporter is
/// executed with a real Python, the resulting <c>design.gds</c> is read back with gdstk,
/// and its polygons are drawn with the exact zoom/pan transform of the canvas crop — so
/// every waveguide, bend, and golden metal trace sits at the same pixel in both panes.
/// Opt-in via <c>UI_SHOT_DIR</c>; skips silently without a gdsfactory-capable Python (CI).
/// </summary>
[Trait("Category", "Showcase")]
[Collection("LocalizationSingleton")]
public class ShowcaseCanvasVsGdsScreenshotTests
{
    /// <summary>World-space (µm) window onto the chip's MZI core: 1x2 splitter, the
    /// phase-shifter and DBR arms with their probe/bond pads and metal traces, and the
    /// 2x2 combiner — compact enough that both panes stay readable side by side. Starts
    /// right of the input waveguide's length label and below the chip-boundary line
    /// (both would be cut mid-glyph at the crop edge).</summary>
    private static readonly (double X, double Y, double W, double H) Region = (150, 22, 1080, 598);

    /// <summary>Extra world-space slack the VIEW is fitted with (beyond <see cref="Region"/>),
    /// so the crop stays clear of the canvas edges — keeping the canvas' bottom-left
    /// status HUD out of the frame instead of slicing through its text.</summary>
    private const double ViewSlackMicrometers = 30;

    /// <summary>Pixel padding kept around the region in both panes.</summary>
    private const double PanePaddingPx = 26;

    [AvaloniaFact]
    public async Task CaptureCanvasVsGds()
    {
        if (!ShowcaseCapture.Enabled) return;
        var python = ShowcaseExportedGds.FindGdsFactoryPython();
        if (python == null) return;   // no gdsfactory env (CI) — the asset is committed

        await ShowcaseCapture.WithEnglishUiAsync(async () =>
        {
            var (vm, window, _) = await ShowcaseCircuit.BootStagedMainWindowAsync();
            UiInput.Descendants<Border>(window)
                .First(b => b.Name == "RightPanelBorder").IsVisible = false;
            Dispatcher.UIThread.RunJobs();
            ShowcaseCircuit.SetView(window, vm, (
                Region.X - ViewSlackMicrometers, Region.Y - ViewSlackMicrometers,
                Region.W + 2 * ViewSlackMicrometers, Region.H + 2 * ViewSlackMicrometers));
            vm.StatusText = "Ready";
            ShowcaseCapture.PumpRenderLoop();

            // The REAL export path: the same script the Export menu writes for this canvas.
            var script = new GdsFactoryExporter().Export(
                vm.Canvas, new GdsFactoryExportOptions(GdsFactoryComponentMode.StandaloneStubs));

            var (crop, originWorld, zoom) = CanvasCrop(window, vm);
            using var canvasFrame = ShowcaseCapture.CaptureFrame(window, "canvas-vs-gds (canvas)");
            window.Close();
            Dispatcher.UIThread.RunJobs();

            var polygons = await ShowcaseExportedGds.RunAndExtractPolygonsAsync(python, script);
            polygons.Count.ShouldBeGreaterThan(20, "the exported GDS must contain the chip's geometry");
            polygons.ShouldContain(p => p.Layer == 11, "the DC block must export as metal traces");

            using var gdsPane = ShowcaseExportedGds.RenderPane(
                polygons, new PixelSize(crop.Width, crop.Height),
                originWorld.X, originWorld.Y, zoom);

            ShowcaseExportedGds.ComposeLabeledSideBySide(
                Path.Combine(ShowcaseCapture.OutputDirectory(), "canvas-vs-gds.png"),
                new[]
                {
                    ((Bitmap)canvasFrame, crop, "Canvas"),
                    (gdsPane, new PixelRect(0, 0, crop.Width, crop.Height),
                        "Exported GDS (gdsfactory)"),
                });
        });
    }

    /// <summary>
    /// The window-space crop of the viewed region (plus padding) inside the design
    /// canvas, and the world origin/zoom of that crop — the shared transform both panes
    /// are rendered with.
    /// </summary>
    private static (PixelRect Crop, Point OriginWorld, double Zoom) CanvasCrop(
        Window window, CAP.Avalonia.ViewModels.MainViewModel vm)
    {
        var canvasControl = window.GetVisualDescendants()
            .OfType<CAP.Avalonia.Controls.DesignCanvas>().First();
        double zoom = canvasControl.Zoom;
        var canvasBounds = ShowcaseCapture.BoundsIn(window, canvasControl);

        var crop = new PixelRect(
            canvasBounds.X + (int)(Region.X * zoom + vm.Canvas.PanX - PanePaddingPx),
            canvasBounds.Y + (int)(Region.Y * zoom + vm.Canvas.PanY - PanePaddingPx),
            (int)(Region.W * zoom + 2 * PanePaddingPx),
            (int)(Region.H * zoom + 2 * PanePaddingPx));
        crop = crop.Intersect(canvasBounds);

        var originWorld = new Point(
            Region.X - PanePaddingPx / zoom, Region.Y - PanePaddingPx / zoom);
        return (crop, originWorld, zoom);
    }
}
