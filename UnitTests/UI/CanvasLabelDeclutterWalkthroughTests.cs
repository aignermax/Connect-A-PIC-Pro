using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CAP.Avalonia.Controls;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Core;
using CAP_Core.Routing;
using Shouldly;
using UnitTests.Helpers;
using UnitTests.Routing.CrossingInsertion;
using Xunit;
using AvaloniaFactAttribute = Avalonia.Headless.XUnit.AvaloniaFactAttribute;

namespace UnitTests.UI;

/// <summary>
/// Visual walkthrough for canvas label declutter: renders three tightly-spaced pairs of
/// terminal components (so each pair's name labels are guaranteed to overlap) connected by
/// routed waveguides, then captures the production <see cref="CAP.Avalonia.Controls.Rendering.WaveguideConnectionRenderer"/>
/// and <see cref="CAP.Avalonia.Controls.Rendering.ComponentRenderer"/> output in four states —
/// zoomed-out overview, zoomed-in (font cap), hover (length reveal), and selection (name-overlap
/// priority) — as step-ordered PNGs + manifest.json in
/// <c>artifacts/ui-screenshots/canvas-label-declutter/</c>.
/// </summary>
[Trait("Category", "UiScreenshots")]
[Collection("LocalizationSingleton")]
public class CanvasLabelDeclutterWalkthroughTests
{
    private const int MinDistinctSampledColors = 4;
    private const int SampleGridSize = 64;
    private const int CanvasWidthPixels = 1000;

    /// <summary>Overview: all three pairs, generous margin for labels and pin indicators
    /// (extra width on the right so "CombinerFinalOutput", the widest label, isn't clipped).</summary>
    private static readonly Rect OverviewViewport = new(-20, -25, 640, 90);

    /// <summary>Close-up on the middle pair only, near the app's zoom clamp (10x, see
    /// <c>DesignCanvas.OnPointerWheelChanged</c>) to stress-test the font-size cap.</summary>
    private static readonly Rect ZoomedInViewport = new(200, -15, 120, 60);

    [AvaloniaFact]
    public void CaptureCanvasLabelDeclutterWalkthrough()
    {
        // Opt-in like UiScreenshotTests: full headless frame captures run only when
        // screenshots are explicitly requested via UI_SHOT_DIR.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("UI_SHOT_DIR")))
            return;
        var dir = ResolveOutputDirectory();
        Directory.CreateDirectory(dir);
        foreach (var stale in Directory.GetFiles(dir, "*.png"))
            File.Delete(stale);

        var canvas = new DesignCanvasViewModel();
        canvas.InitializeAStarRouting();
        var scene = BuildScene(canvas);
        var state = new CanvasInteractionState();
        var manifest = new List<object>();

        // Step 1: zoomed-out overview. Names stay visible for orientation, but the
        // lower-priority name in each tightly-spaced pair is dropped rather than overlapping —
        // and with nothing hovered or selected, no connection shows its length/loss label.
        Capture(canvas, state, OverviewViewport, dir, "01-overview-no-length-thinned-names.png");
        manifest.Add(new
        {
            file = "01-overview-no-length-thinned-names.png",
            caption = "Overview: three tightly-spaced component pairs. No connection shows a "
                + "length/loss label (nothing hovered or selected) and, within each pair whose "
                + "names would overlap, only the higher-priority name is drawn — the rest stay "
                + "hidden rather than turn into illegible overlapping text.",
        });

        // Step 2: zoomed in close on the middle pair, near the app's zoom clamp. Without a
        // screen-space cap the 12-14pt world-space font would balloon to ~100+ screen pixels;
        // capped, it stays a small, legible, screen-constant size.
        Capture(canvas, state, ZoomedInViewport, dir, "02-zoomed-in-font-capped.png");
        manifest.Add(new
        {
            file = "02-zoomed-in-font-capped.png",
            caption = "Zoomed in close on the middle pair (~8x, near the app's 10x zoom clamp). "
                + "Name labels stay a small, legible, screen-constant size instead of ballooning "
                + "with zoom — PinScreenSize.CapWorldFontSize applied to label text.",
        });

        // Step 3: hovering a connection reveals its length/loss label on demand — the detail
        // information is still one hover away, it just isn't clutter by default anymore.
        state.HoveredConnection = scene.Pair1Connection;
        Capture(canvas, state, OverviewViewport, dir, "03-hover-reveals-length.png");
        manifest.Add(new
        {
            file = "03-hover-reveals-length.png",
            caption = "Hovering the first pair's connection reveals its length/loss label — "
                + "the only connection with a label here, since it is the only one hovered "
                + "or selected.",
        });
        state.HoveredConnection = null;

        // Step 4: selecting the pair-1 component that LOST the overlap tie-break in step 1
        // ("SourceLaserInput") now wins over its unselected partner — selected outranks the
        // deterministic ordinal tie-break, and both hovered and selected outrank plain "rest".
        scene.SourceLaserInput.IsSelected = true;
        Capture(canvas, state, OverviewViewport, dir, "04-selection-wins-name-overlap.png");
        manifest.Add(new
        {
            file = "04-selection-wins-name-overlap.png",
            caption = "Selecting 'SourceLaserInput' — the name that lost pair 1's overlap "
                + "tie-break in step 1 — now shows it instead of 'PhotoDetectorOutput': "
                + "selected always outranks hovered, which always outranks the rest.",
        });

        File.WriteAllText(
            Path.Combine(dir, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Three terminal pairs, each internally 40 µm apart (label-overlap guaranteed)
    /// and 170+ µm apart from the next pair (no cross-pair overlap), each pair joined by a
    /// straight routed waveguide built from the pins' own absolute positions so the route is
    /// never flagged stale.</summary>
    private sealed record Scene(ComponentViewModel SourceLaserInput, WaveguideConnectionViewModel Pair1Connection);

    private static Scene BuildScene(DesignCanvasViewModel canvas)
    {
        var a = AddTerminal(canvas, "SourceLaserInput", 0, 0, angleDegrees: 0);
        var b = AddTerminal(canvas, "PhotoDetectorOutput", 40, 0, angleDegrees: 180);
        var c = AddTerminal(canvas, "AmplifierBoostStage", 220, 0, angleDegrees: 0);
        var d = AddTerminal(canvas, "WaveguideTapMonitor", 260, 0, angleDegrees: 180);
        var e = AddTerminal(canvas, "ModulatorPhaseShift", 440, 0, angleDegrees: 0);
        var f = AddTerminal(canvas, "CombinerFinalOutput", 480, 0, angleDegrees: 180);

        var pair1Connection = ConnectStraight(canvas, a, b);
        ConnectStraight(canvas, c, d);
        ConnectStraight(canvas, e, f);

        var sourceLaserInputVm = canvas.Components.Single(vm => vm.Component == a.Component);
        return new Scene(sourceLaserInputVm, pair1Connection);
    }

    /// <summary>Builds a straight cached route between two terminals' own pin positions, so the
    /// route can never be flagged stale (its endpoints match the pins exactly).</summary>
    private static WaveguideConnectionViewModel ConnectStraight(
        DesignCanvasViewModel canvas, CrossingTestCircuit.Terminal left, CrossingTestCircuit.Terminal right)
    {
        var (startX, startY) = left.PhysicalPin.GetAbsolutePosition();
        var (endX, endY) = right.PhysicalPin.GetAbsolutePosition();
        var route = new RoutedPath();
        route.Segments.Add(new StraightSegment(startX, startY, endX, endY, 0));
        return canvas.ConnectPinsWithCachedRoute(left.PhysicalPin, right.PhysicalPin, route)!;
    }

    private static CrossingTestCircuit.Terminal AddTerminal(
        DesignCanvasViewModel canvas, string name, double x, double y, double angleDegrees)
    {
        var terminal = CrossingTestCircuit.CreateTerminal(name, x, y, angleDegrees);
        canvas.AddComponent(terminal.Component, "Terminal");
        return terminal;
    }

    private static void Capture(DesignCanvasViewModel canvas, CanvasInteractionState state,
        Rect world, string outputDir, string filename)
    {
        var scene = new CanvasLabelDeclutterSceneControl(canvas, state, world)
        {
            Width = CanvasWidthPixels,
            Height = CanvasWidthPixels * world.Height / world.Width,
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

    /// <summary>Repo-root <c>artifacts/ui-screenshots/canvas-label-declutter</c>, or
    /// <c>UI_SHOT_DIR/canvas-label-declutter</c>.</summary>
    private static string ResolveOutputDirectory()
    {
        var envDir = Environment.GetEnvironmentVariable("UI_SHOT_DIR");
        if (!string.IsNullOrEmpty(envDir))
            return Path.Combine(envDir, "canvas-label-declutter");

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
                return Path.Combine(dir.FullName, "artifacts", "ui-screenshots", "canvas-label-declutter");
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "artifacts", "ui-screenshots", "canvas-label-declutter");
    }
}
