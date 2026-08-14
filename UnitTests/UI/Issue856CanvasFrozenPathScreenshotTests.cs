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
/// Visual walkthrough for issue #856 (ungroup must not lose GDS-imported route geometry):
/// renders the real <see cref="DesignCanvas"/> through four states — group with a pin-less
/// imported route, after ungroup (geometry preserved on the canvas), route selected
/// (yellow highlight), and after a drag-move — as step-ordered PNGs + manifest.json in
/// <c>UI_SHOT_DIR/issue-856/</c>.
/// </summary>
[Trait("Category", "UiScreenshots")]
[Collection("LocalizationSingleton")]
public class Issue856CanvasFrozenPathScreenshotTests
{
    private const int WindowWidth = 1000;
    private const int WindowHeight = 700;
    private const double CanvasZoom = 3.0;
    private const int CaptureAttempts = 3;

    /// <summary>Captures the four walkthrough states and writes the caption manifest.</summary>
    [AvaloniaFact]
    public void CaptureIssue856UngroupWalkthrough()
    {
        // Opt-in like UiScreenshotTests: only runs when screenshots are explicitly requested.
        var shotRoot = Environment.GetEnvironmentVariable("UI_SHOT_DIR");
        if (string.IsNullOrEmpty(shotRoot))
            return;
        var outputDir = Path.Combine(shotRoot, "issue-856");
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

        var designCanvas = new DesignCanvas { ViewModel = canvas, MainViewModel = vm, Zoom = CanvasZoom };
        var window = new Window { Width = WindowWidth, Height = WindowHeight, Content = designCanvas };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var manifest = new List<object>();

        try
        {
            Capture(designCanvas, window, Path.Combine(outputDir, "01-group-with-imported-route.png"));
            manifest.Add(new
            {
                file = "01-group-with-imported-route.png",
                caption = "GDS import wrapped the placed components in a group; the top-cell route "
                    + "polygon rides along as pin-less frozen geometry, drawn in its source-layer color.",
            });

            vm.CommandManager.ExecuteCommand(new UngroupCommand(canvas, group));
            canvas.CanvasFrozenPaths.Count.ShouldBe(1);
            Capture(designCanvas, window, Path.Combine(outputDir, "02-after-ungroup-geometry-preserved.png"));
            manifest.Add(new
            {
                file = "02-after-ungroup-geometry-preserved.png",
                caption = "Ctrl+Shift+G ungroups: the components are released AND the imported route "
                    + "geometry survives on the canvas (previously it vanished permanently). "
                    + "Undo restores the group with its original path.",
            });

            vm.CanvasInteraction.CanvasClicked(140, 120);
            vm.CanvasInteraction.SelectedCanvasFrozenPath.ShouldNotBeNull();
            Capture(designCanvas, window, Path.Combine(outputDir, "03-selected-route-geometry.png"));
            manifest.Add(new
            {
                file = "03-selected-route-geometry.png",
                caption = "Clicking the released route selects it (yellow highlight, same visual "
                    + "language as waveguide connections); Delete/Backspace removes it undoably.",
            });

            // Mirror the drag recognizer: the live drag translates the geometry, then the
            // command records the total delta for undo/redo.
            var pathVm = canvas.CanvasFrozenPaths[0];
            pathVm.Path.TranslateBy(30, 25);
            vm.CommandManager.ExecuteCommand(new MoveCanvasFrozenPathCommand(pathVm, 30, 25));
            Capture(designCanvas, window, Path.Combine(outputDir, "04-moved-route-geometry.png"));
            manifest.Add(new
            {
                file = "04-moved-route-geometry.png",
                caption = "The released geometry stays a first-class canvas citizen: dragging moves "
                    + "it as one undoable command, and it persists in the .lun file and GDS export.",
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
        return new FrozenWaveguidePath { Path = path, Layer = 31, DataType = 5 };
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
