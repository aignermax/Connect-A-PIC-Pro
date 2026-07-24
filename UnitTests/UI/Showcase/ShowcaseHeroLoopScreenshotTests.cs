using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.Views;
using Shouldly;
using UnitTests.UI.Flows;
using Xunit;

namespace UnitTests.UI.Showcase;

/// <summary>
/// v0.12 feature-showcase: frames for the README hero loop (<c>hero-loop.gif</c>). Stages
/// the hero chip LIVE in the real MainWindow: every photonic component placed, then the
/// production router wiring connection after connection (diagonal pathfinding on), the DC
/// pads and golden metal traces, the two hand-styled bends — and as the payoff the real
/// CW simulation lighting up the power-flow overlay. One PNG per build step lands in
/// <c>UI_SHOT_DIR/v0.12/hero-loop-frames/</c>; assemble the GIF with
/// <c>scripts/assemble_hero_loop.py</c> (Pillow). Opt-in via <c>UI_SHOT_DIR</c>.
/// </summary>
[Trait("Category", "Showcase")]
[Collection("LocalizationSingleton")]
public class ShowcaseHeroLoopScreenshotTests
{
    /// <summary>World-space (µm) crop shared by every frame — the full staged chip.</summary>
    private static readonly (double X, double Y, double W, double H) Region = (10, -5, 1530, 650);

    /// <summary>1 placement + 8 optical + 1 pads + 4 electrical + 1 styled/routed + 1 simulation.</summary>
    private const int ExpectedFrameCount = 16;

    [AvaloniaFact]
    public async Task CaptureHeroLoopFrames()
    {
        if (!ShowcaseCapture.Enabled) return;
        await ShowcaseCapture.WithEnglishUiAsync(async () =>
        {
            var vm = ShowcaseCircuit.CreateStagedViewModel();
            var window = ShowcaseCircuit.BootMainWindow(vm);
            UiInput.Descendants<Border>(window)
                .First(b => b.Name == "RightPanelBorder").IsVisible = false;
            Dispatcher.UIThread.RunJobs();
            ShowcaseCircuit.FitView(window, vm);

            var framesDir = Path.Combine(ShowcaseCapture.OutputDirectory(), "hero-loop-frames");
            Directory.CreateDirectory(framesDir);
            foreach (var stale in Directory.GetFiles(framesDir, "frame_*.png"))
                File.Delete(stale);

            int frame = 0;
            Task CaptureStepAsync()
            {
                SaveCanvasCrop(window, vm, Path.Combine(framesDir, $"frame_{frame++:D2}.png"));
                return Task.CompletedTask;
            }

            await ShowcaseCircuit.BuildChipAsync(vm, CaptureStepAsync);

            // The payoff frame: the CW run lights up the power-flow overlay.
            await vm.RunSimulationCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            vm.Canvas.ShowPowerFlow.ShouldBeTrue("the CW run must enable the power-flow overlay");
            await CaptureStepAsync();

            frame.ShouldBe(ExpectedFrameCount,
                "the assembly script's timing assumes this exact frame sequence");
            window.Close();
            Dispatcher.UIThread.RunJobs();
        });
    }

    /// <summary>Captures the window and writes the canvas crop of <see cref="Region"/> —
    /// identical view transform in every frame, so the loop holds perfectly still.</summary>
    private static void SaveCanvasCrop(MainWindow window, MainViewModel vm, string path)
    {
        ShowcaseCapture.PumpRenderLoop();
        var canvasControl = window.GetVisualDescendants()
            .OfType<CAP.Avalonia.Controls.DesignCanvas>().First();
        double zoom = canvasControl.Zoom;
        var canvasBounds = ShowcaseCapture.BoundsIn(window, canvasControl);
        var crop = new PixelRect(
            canvasBounds.X + (int)(Region.X * zoom + vm.Canvas.PanX),
            canvasBounds.Y + (int)(Region.Y * zoom + vm.Canvas.PanY),
            (int)(Region.W * zoom), (int)(Region.H * zoom))
            .Intersect(canvasBounds);

        using var frame = ShowcaseCapture.CaptureFrame(window, Path.GetFileName(path));
        using var plate = new RenderTargetBitmap(new PixelSize(crop.Width, crop.Height));
        using (var ctx = plate.CreateDrawingContext())
        {
            ctx.DrawImage(frame,
                new Rect(crop.X, crop.Y, crop.Width, crop.Height),
                new Rect(0, 0, crop.Width, crop.Height));
        }
        ScreenshotArtifacts.SavePng(plate, path);
    }
}
