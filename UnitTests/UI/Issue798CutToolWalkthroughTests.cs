using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Controls;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Canvas.CrossingInsertion;
using CAP_Core.Routing;
using Shouldly;
using UnitTests.Helpers;
using UnitTests.Routing.CrossingInsertion;
using Xunit;
using AvaloniaFactAttribute = Avalonia.Headless.XUnit.AvaloniaFactAttribute;

namespace UnitTests.UI;

/// <summary>
/// Visual walkthrough for the Cut tool (manual crossing insertion): renders the
/// production <see cref="CAP.Avalonia.Controls.Canvas.CutTool.CutToolOverlayRenderer"/>
/// over a waveguide with a pin guide line in four states — armed with guides, hovered
/// candidate, inserted crossing, and after undo — as step-ordered PNGs + manifest.json in
/// <c>artifacts/ui-screenshots/issue-798/</c>.
/// </summary>
[Trait("Category", "UiScreenshots")]
[Collection("LocalizationSingleton")]
public class Issue798CutToolWalkthroughTests
{
    private const int MinDistinctSampledColors = 4;
    private const int SampleGridSize = 64;
    private const int CanvasWidthPixels = 1000;

    /// <summary>Fixed world viewport (µm) so all frames are comparable.</summary>
    private static readonly Rect Viewport = new(-20, 20, 440, 220);

    /// <summary>Captures the four walkthrough steps and writes the caption manifest.</summary>
    [AvaloniaFact]
    public void CaptureIssue798CutToolWalkthrough()
    {
        // Opt-in like UiScreenshotTests: full headless frame captures run only when
        // screenshots are explicitly requested via UI_SHOT_DIR.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("UI_SHOT_DIR")))
            return;
        var dir = ResolveOutputDirectory();
        Directory.CreateDirectory(dir);
        foreach (var stale in Directory.GetFiles(dir, "*.png"))
            File.Delete(stale);

        var vm = MainViewModelTestHelper.CreateMainViewModel();
        var templates = TestPdkLoader.LoadFromPdk("siepic-ebeam-pdk.json");
        vm.LeftPanel.AllTemplates.Add(templates.Single(t => string.Equals(
            t.NazcaFunctionName, CrossingComponentInstance.CrossingNazcaFunctionName,
            StringComparison.OrdinalIgnoreCase)));
        var original = BuildScene(vm);
        vm.CanvasInteraction.SetCutModeCommand.Execute(null);
        var state = new CanvasInteractionState();
        var manifest = new List<object>();

        // Step 1: arming the Cut tool shows dashed guide rays from visible pins and a
        // circular candidate marker where a ray crosses a perpendicular waveguide.
        Capture(vm, state, dir, "01-cut-mode-guides-and-candidate.png");
        manifest.Add(new
        {
            file = "01-cut-mode-guides-and-candidate.png",
            caption = "Cut mode armed (toolbar scissors or X): dashed guide lines extend from "
                + "the pins of visible components along their pin axis; where a guide crosses a "
                + "perpendicular waveguide with enough straight run, a circular candidate marker "
                + "appears.",
        });

        // Step 2: hovering the candidate fills and enlarges it — the click target is unmistakable.
        state.HoveredCutCandidate = state.CutCandidates.ShouldHaveSingleItem();
        Capture(vm, state, dir, "02-hovered-candidate.png");
        manifest.Add(new
        {
            file = "02-hovered-candidate.png",
            caption = "Hovering a candidate fills and enlarges the marker (screen-constant size), "
                + "so the exact insertion point is unambiguous before clicking.",
        });

        // Step 3: clicking inserts the PDK crossing centered on the intersection and splits
        // the connection into two halves docked onto the crossing's through ports.
        var instance = CrossingComponentInstance.CreateFromTemplates(vm.LeftPanel.AllTemplates)!;
        vm.CommandManager.ExecuteCommand(
            new InsertManualCrossingCommand(vm.Canvas, state.HoveredCutCandidate!, instance));
        state.HoveredCutCandidate = null;
        Capture(vm, state, dir, "03-crossing-inserted.png");
        manifest.Add(new
        {
            file = "03-crossing-inserted.png",
            caption = "Clicking inserts the PDK crossing (ebeam_crossing4) centered on the "
                + "intersection; the waveguide splits into two connections docked onto the "
                + "crossing's through ports. The crossing is user intent — the adaptive pass "
                + "never dissolves it.",
        });

        // Step 4: one undo removes the crossing and restores the original connection object.
        vm.CommandManager.Undo().ShouldBeTrue();
        vm.Canvas.ConnectionManager.Connections.ShouldHaveSingleItem().ShouldBeSameAs(original);
        Capture(vm, state, dir, "04-undo-restores-connection.png");
        manifest.Add(new
        {
            file = "04-undo-restores-connection.png",
            caption = "The insertion is one undoable command: Ctrl+Z removes the crossing and "
                + "restores the original connection with all fine-tuning settings preserved.",
        });

        File.WriteAllText(
            Path.Combine(dir, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Horizontal waveguide (10,100)→(390,100) plus a guide terminal above (200,100).</summary>
    private static CAP_Core.Components.Connections.WaveguideConnection BuildScene(MainViewModel vm)
    {
        var left = CrossingTestCircuit.CreateTerminal("left", 0, 95, pinAngleDegrees: 0);
        var right = CrossingTestCircuit.CreateTerminal("right", 380, 95, pinAngleDegrees: 180);
        var guide = CrossingTestCircuit.CreateTerminal("guide", 195, 40, pinAngleDegrees: 90);
        vm.Canvas.AddComponent(left.Component, "Terminal");
        vm.Canvas.AddComponent(right.Component, "Terminal");
        vm.Canvas.AddComponent(guide.Component, "Terminal");

        var route = new RoutedPath();
        route.Segments.Add(new StraightSegment(10, 100, 390, 100, 0));
        return vm.Canvas.ConnectPinsWithCachedRoute(left.PhysicalPin, right.PhysicalPin, route)!
            .Connection;
    }

    private static void Capture(MainViewModel vm, CanvasInteractionState state,
        string outputDir, string filename)
    {
        var scene = new Issue798CutToolSceneControl(vm, state, Viewport)
        {
            Width = CanvasWidthPixels,
            Height = CanvasWidthPixels * Viewport.Height / Viewport.Width,
        };
        var window = new Window
        {
            Width = scene.Width,
            Height = scene.Height,
            Content = scene,
            Background = Brushes.Black,
        };
        window.Show();
        try
        {
            WriteableBitmap? bitmap = null;
            for (var attempt = 0; attempt < 3 && bitmap == null; attempt++)
            {
                Dispatcher.UIThread.RunJobs();
                bitmap = window.CaptureRenderedFrame();
            }
            bitmap.ShouldNotBeNull($"CaptureRenderedFrame stayed null after 3 attempts for {filename}");
            using (bitmap)
            {
                CountDistinctSampledColors(bitmap).ShouldBeGreaterThan(MinDistinctSampledColors,
                    $"Near-blank render for {filename} — likely a missing Skia setup.");
                bitmap.Save(Path.Combine(outputDir, filename));
            }
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static int CountDistinctSampledColors(WriteableBitmap bitmap)
    {
        using var fb = bitmap.Lock();
        int width = fb.Size.Width;
        int height = fb.Size.Height;
        if (width <= 0 || height <= 0) return 0;

        int stepX = Math.Max(1, width / SampleGridSize);
        int stepY = Math.Max(1, height / SampleGridSize);
        var colors = new HashSet<int>();
        for (int y = 0; y < height; y += stepY)
        {
            var rowAddr = fb.Address + y * fb.RowBytes;
            for (int x = 0; x < width; x += stepX)
                colors.Add(Marshal.ReadInt32(rowAddr, x * 4));
        }
        return colors.Count;
    }

    /// <summary>Repo-root <c>artifacts/ui-screenshots/issue-798</c>, or <c>UI_SHOT_DIR/issue-798</c>.</summary>
    private static string ResolveOutputDirectory()
    {
        var envDir = Environment.GetEnvironmentVariable("UI_SHOT_DIR");
        if (!string.IsNullOrEmpty(envDir))
            return Path.Combine(envDir, "issue-798");

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
                return Path.Combine(dir.FullName, "artifacts", "ui-screenshots", "issue-798");
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "artifacts", "ui-screenshots", "issue-798");
    }
}
