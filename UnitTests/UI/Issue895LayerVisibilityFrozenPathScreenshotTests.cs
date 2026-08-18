using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Controls;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Core;
using CAP_Core.Routing;
using Shouldly;
using UnitTests.Analysis.AnalysisOutput;
using UnitTests.Helpers;
using Xunit;
using AvaloniaFactAttribute = Avalonia.Headless.XUnit.AvaloniaFactAttribute;

namespace UnitTests.UI;

/// <summary>
/// Visual walkthrough for issue #895 (layer visibility filter must cover canvas-level
/// frozen paths): renders the real <see cref="DesignCanvas"/> through three states —
/// an ungrouped imported route visible on the canvas, the same route hidden via its
/// Imported Layers row, and the route faded at reduced opacity — as step-ordered
/// PNGs + manifest.json in <c>UI_SHOT_DIR/issue-895/</c>.
/// </summary>
[Trait("Category", "UiScreenshots")]
[Collection("LocalizationSingleton")]
public class Issue895LayerVisibilityFrozenPathScreenshotTests
{
    private const int WindowWidth = 1000;
    private const int WindowHeight = 700;
    private const double CanvasZoom = 3.0;
    private const int CaptureAttempts = 3;
    private const int ImportLayer = 31;
    private const int ImportDataType = 5;
    private const double FadedOpacityPercent = 30.0;

    /// <summary>Captures the three walkthrough states and writes the caption manifest.</summary>
    [AvaloniaFact]
    public void CaptureIssue895LayerVisibilityWalkthrough()
    {
        // Opt-in like UiScreenshotTests: only runs when screenshots are explicitly requested.
        var shotRoot = Environment.GetEnvironmentVariable("UI_SHOT_DIR");
        if (string.IsNullOrEmpty(shotRoot))
            return;
        var outputDir = Path.Combine(shotRoot, "issue-895");
        Directory.CreateDirectory(outputDir);
        foreach (var stale in Directory.GetFiles(outputDir, "*.png"))
            File.Delete(stale);

        var canvas = new DesignCanvasViewModel();
        var comp1 = AnalysisOutputTestBed.AddPlainComponent(canvas, x: 60, y: 60);
        var comp2 = AnalysisOutputTestBed.AddPlainComponent(canvas, x: 160, y: 60);
        var vm = MainViewModelTestHelper.CreateMainViewModel(canvas: canvas);

        canvas.Selection.AddToSelection(comp1);
        canvas.Selection.AddToSelection(comp2);
        vm.CommandManager.ExecuteCommand(
            new CreateGroupCommand(canvas, canvas.Selection.SelectedComponents.ToList()));
        var group = (ComponentGroup)canvas.Components
            .Single(c => c.Component is ComponentGroup).Component;
        group.AddInternalPath(ImportedRouteOutline());
        canvas.Selection.ClearSelection();
        vm.CommandManager.ExecuteCommand(new UngroupCommand(canvas, group));
        canvas.CanvasFrozenPaths.Count.ShouldBe(1);

        var designCanvas = new DesignCanvas { ViewModel = canvas, MainViewModel = vm, Zoom = CanvasZoom };
        var window = new Window { Width = WindowWidth, Height = WindowHeight, Content = designCanvas };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var manifest = new List<object>();

        try
        {
            // The queued panel refresh (CanvasFrozenPaths change) runs on the dispatcher.
            Dispatcher.UIThread.RunJobs();
            var row = vm.LayerVisibility.Rows
                .Single(r => r.Layer == ImportLayer && r.DataType == ImportDataType);

            Capture(designCanvas, window, Path.Combine(outputDir, "01-ungrouped-route-visible.png"));
            manifest.Add(new
            {
                file = "01-ungrouped-route-visible.png",
                caption = "After a full ungroup the imported route lives directly on the canvas; "
                    + "the Imported Layers panel now counts this canvas-level geometry, so its "
                    + "layer row exists even without any remaining group.",
            });

            row.IsVisible = false;
            Capture(designCanvas, window, Path.Combine(outputDir, "02-layer-hidden.png"));
            manifest.Add(new
            {
                file = "02-layer-hidden.png",
                caption = "Hiding the layer in the Imported Layers panel now also hides the "
                    + "canvas-level frozen path (previously it ignored the filter and stayed drawn).",
            });

            row.IsVisible = true;
            row.OpacityPercent = FadedOpacityPercent;
            Capture(designCanvas, window, Path.Combine(outputDir, "03-layer-faded.png"));
            manifest.Add(new
            {
                file = "03-layer-faded.png",
                caption = "The per-layer opacity slider applies too: the released route draws "
                    + "faded at 30% like every other shape on its layer.",
            });
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }

        File.WriteAllText(
            Path.Combine(outputDir, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Long thin closed rectangle outline (40,118)→(240,126) — the shape GDS import
    /// traces from a top-cell route polygon — tagged with its source layer.</summary>
    private static FrozenWaveguidePath ImportedRouteOutline()
    {
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(40, 118, 240, 118, 0));
        path.Segments.Add(new StraightSegment(240, 118, 240, 126, 90));
        path.Segments.Add(new StraightSegment(240, 126, 40, 126, 180));
        path.Segments.Add(new StraightSegment(40, 126, 40, 118, -90));
        return new FrozenWaveguidePath { Path = path, Layer = ImportLayer, DataType = ImportDataType };
    }

    private static void Capture(DesignCanvas designCanvas, Window window, string path)
    {
        Dispatcher.UIThread.RunJobs();
        designCanvas.InvalidateVisual();
        Dispatcher.UIThread.RunJobs();

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
