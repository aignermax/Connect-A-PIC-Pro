using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CAP.Avalonia.Controls;
using CAP.Avalonia.Views.Panels;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Panels;
using Shouldly;
using UnitTests.Analysis.AnalysisOutput;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Visual documentation for the analysis-output picker (#754): renders the real design
/// canvas (with the interaction-mode-aware overlays) above the real analysis dock, with
/// three couplers — one input (laser on) and two off-laser candidates — and captures
/// (1) the picker mode with glowing candidates and (2) the designated output carrying
/// its "OUT" tag while the dock header shows its name. PNGs land in
/// <c>UI_SHOT_DIR/analysis-output/</c>.
/// </summary>
[Trait("Category", "UiScreenshots")]
[Collection("LocalizationSingleton")]
public class Issue754AnalysisOutputScreenshotTests
{
    private const int WindowWidth = 1000;
    private const int WindowHeight = 760;
    private const double CanvasZoom = 3.0;
    private const int CaptureAttempts = 3;

    /// <summary>Captures the picker-mode and designated-output states.</summary>
    [AvaloniaFact]
    public void CaptureAnalysisOutputPickerStates()
    {
        // Opt-in like UiScreenshotTests: only runs when screenshots are explicitly requested.
        var shotRoot = Environment.GetEnvironmentVariable("UI_SHOT_DIR");
        if (string.IsNullOrEmpty(shotRoot))
            return;
        var outputDir = Path.Combine(shotRoot, "analysis-output");
        Directory.CreateDirectory(outputDir);
        foreach (var stale in Directory.GetFiles(outputDir, "*.png"))
            File.Delete(stale);

        var canvas = new DesignCanvasViewModel();
        var input = AnalysisOutputTestBed.AddCoupler(canvas, x: 60, y: 60);
        input.Component.HumanReadableName = "GC in";
        var candidateA = AnalysisOutputTestBed.AddCoupler(canvas, x: 200, y: 40);
        candidateA.Component.HumanReadableName = "GC out A";
        candidateA.LaserConfig!.IsEnabled = false;
        var candidateB = AnalysisOutputTestBed.AddCoupler(canvas, x: 200, y: 120);
        candidateB.Component.HumanReadableName = "GC out B";
        candidateB.LaserConfig!.IsEnabled = false;

        var vm = MainViewModelTestHelper.CreateMainViewModel(canvas: canvas);
        vm.BottomPanel.Analysis.IsVisible = true;

        // Compose the REAL canvas control (mode-aware overlays need MainViewModel, which
        // MainView does not wire) above the REAL analysis dock with its output header.
        var designCanvas = new DesignCanvas { ViewModel = canvas, MainViewModel = vm, Zoom = CanvasZoom };
        var dock = new AnalysisDockPanel { DataContext = vm };
        var root = new DockPanel();
        DockPanel.SetDock(dock, global::Avalonia.Controls.Dock.Bottom);
        root.Children.Add(dock);
        root.Children.Add(designCanvas);
        var window = new Window
        {
            Width = WindowWidth,
            Height = WindowHeight,
            Content = root,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            // State 1: picker active — both off-laser candidates glow on the canvas.
            vm.CanvasInteraction.SetPickAnalysisOutputModeCommand.Execute(null);
            RepaintCanvas(designCanvas);
            CaptureWithRetry(window, Path.Combine(outputDir, "picker-candidates.png"));

            // State 2: one coupler designated — picker done, "OUT" tag on the canvas,
            // dock header shows the coupler's name with the Clear button.
            vm.CanvasInteraction.CanvasClicked(210, 125);
            vm.CanvasInteraction.CurrentMode.ShouldBe(InteractionMode.Select,
                "a successful pick returns to Select mode");
            canvas.AnalysisOutput.CouplerId.ShouldBe(candidateB.Component.Id);
            RepaintCanvas(designCanvas);
            CaptureWithRetry(window, Path.Combine(outputDir, "designated-output.png"));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }

        Directory.GetFiles(outputDir, "*.png").Length.ShouldBe(2);
    }

    private static void RepaintCanvas(DesignCanvas designCanvas)
    {
        Dispatcher.UIThread.RunJobs();
        designCanvas.InvalidateVisual();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Captures the window, pumping the dispatcher before every attempt and keeping the
    /// last successful frame (headless rendering can miss frames — same pattern as
    /// <see cref="RoutingStyleCanvasDiagnosticTests"/>).
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
