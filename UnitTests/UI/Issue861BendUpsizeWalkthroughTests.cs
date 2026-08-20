using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CAP.Avalonia.Controls;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.Views;
using CAP_Core.Routing;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Visual walkthrough for issue #861 (post-routing bend radius upsizing): renders the real
/// design canvas while an Auto connection with generous corner space gets its bends grown to
/// the largest allowed radius (50µm), a sibling route through the same region constrains the
/// pass without creating a crossing, and a tight offset keeps the small process radius.
/// Writes step-ordered PNGs and a <c>manifest.json</c> into
/// <c>artifacts/ui-screenshots/issue-861/</c>. Opt-in via <c>UI_SHOT_DIR</c>.
/// </summary>
[Trait("Category", "UiScreenshots")]
// Heavy Skia frame captures — CI covers it, local default runs exclude Category=Slow.
[Trait("Category", "Slow")]
[Collection("LocalizationSingleton")]
public class Issue861BendUpsizeWalkthroughTests
{
    private const int WindowWidth = 1100;
    private const int WindowHeight = 980;
    private const int CaptureAttempts = 3;
    private const double LargestAllowedRadius = 50.0;

    /// <summary>Captures the three walkthrough states and writes the caption manifest.</summary>
    [AvaloniaFact]
    public async Task CaptureIssue861BendUpsizeWalkthrough()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("UI_SHOT_DIR")))
            return;
        var dir = ResolveOutputDirectory();
        Directory.CreateDirectory(dir);
        foreach (var stale in Directory.GetFiles(dir, "*.png"))
            File.Delete(stale);

        var canvas = new DesignCanvasViewModel();
        canvas.InitializeAStarRouting(0, 0, 1000, 900);
        var vm = MainViewModelTestHelper.CreateMainViewModel(canvas: canvas);

        var manifest = new List<ManifestEntry>();
        var window = BuildWindow(vm);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            await CaptureScenes(window, canvas, dir, manifest);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }

        ScreenshotArtifacts.WriteText(Path.Combine(dir, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            {
                WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));
        manifest.Count.ShouldBe(3);
    }

    /// <summary>Walks the generous-corner → constrained-by-sibling → tight-offset story.</summary>
    private static async Task CaptureScenes(
        Window window, DesignCanvasViewModel canvas, string dir, List<ManifestEntry> manifest)
    {
        var generous = await ConnectPairAsync(canvas, startX: 40, startY: 40, endX: 40, endY: 440);
        await canvas.RecalculateRoutesAsync();
        BendRadii(generous).ShouldContain(LargestAllowedRadius,
            "a corner with generous free space must be upsized to the largest allowed radius");
        RefreshCanvas(window);
        Capture(window, dir, "01-generous-corner-upsized-to-50um.png",
            "A lone Auto connection with plenty of room around its corners: after every route "
            + "is final, the post-pass grows the bends to the largest allowed radius (50 µm) "
            + "for the lowest optical loss.", manifest);

        var sibling = await ConnectPairAsync(canvas, startX: 40, startY: 340, endX: 620, endY: 140);
        await canvas.RecalculateRoutesAsync();
        sibling.Connection.RoutedPath.ShouldNotBeNull();
        sibling.Connection.IsPathValid.ShouldBeTrue();
        generous.Connection.IsPathValid.ShouldBeTrue();
        RefreshCanvas(window);
        Capture(window, dir, "02-sibling-route-constrains-upsizing.png",
            "A second route crossing the same region: each candidate arc is vetoed against ALL "
            + "sibling routes at their final positions, so upsizing never trades a gentler bend "
            + "for a new crossing.", manifest);

        var tight = await ConnectPairAsync(canvas, startX: 40, startY: 640, endX: 340, endY: 670);
        await canvas.RecalculateRoutesAsync();
        BendRadii(tight).ShouldNotBeEmpty();
        BendRadii(tight).ShouldAllBe(r => r < LargestAllowedRadius,
            "short straight runs leave no room for the 50 µm tangent length");
        RefreshCanvas(window);
        Capture(window, dir, "03-tight-offset-keeps-small-radius.png",
            "A small pin offset with short straight runs: the pass leaves the smaller process "
            + "radius in place — it only grows a bend when both neighbouring straights can shed "
            + "the extra tangent length.", manifest);
    }

    /// <summary>All bend radii (µm) of the connection's routed path.</summary>
    private static List<double> BendRadii(WaveguideConnectionViewModel connection) =>
        connection.Connection.RoutedPath!.Segments
            .OfType<BendSegment>()
            .Select(b => b.RadiusMicrometers)
            .ToList();

    /// <summary>Places a straight-waveguide pair and connects out → in.</summary>
    private static async Task<WaveguideConnectionViewModel> ConnectPairAsync(
        DesignCanvasViewModel canvas, double startX, double startY, double endX, double endY)
    {
        var start = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        start.PhysicalX = startX;
        start.PhysicalY = startY;
        var end = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        end.PhysicalX = endX;
        end.PhysicalY = endY;
        canvas.AddComponent(start);
        canvas.AddComponent(end);
        var connection = await canvas.ConnectPinsAsync(
            start.PhysicalPins.First(p => p.Name == "out"),
            end.PhysicalPins.First(p => p.Name == "in"));
        connection.ShouldNotBeNull();
        return connection!;
    }

    /// <summary>Canvas-only window; the story is about routed geometry, not panels.</summary>
    private static Window BuildWindow(MainViewModel vm) => new()
    {
        Width = WindowWidth,
        Height = WindowHeight,
        Content = new MainView { DataContext = vm },
    };

    /// <summary>Drains pending repaint jobs and forces a fresh render of the routes.</summary>
    private static void RefreshCanvas(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        foreach (var designCanvas in window.GetVisualDescendants().OfType<DesignCanvas>())
            designCanvas.InvalidateVisual();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Captures the shown window to a PNG (with retry) and records the caption.</summary>
    private static void Capture(
        Window window, string dir, string filename, string caption, List<ManifestEntry> manifest)
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
        bitmap.ShouldNotBeNull(
            $"CaptureRenderedFrame stayed null after {CaptureAttempts} attempts for {filename}");
        using (bitmap)
        {
            ScreenshotArtifacts.SavePng(bitmap, Path.Combine(dir, filename));
        }
        manifest.Add(new ManifestEntry(filename, caption));
    }

    /// <summary>Resolves the walkthrough output directory (repo root's artifacts folder).</summary>
    private static string ResolveOutputDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
                return Path.Combine(dir.FullName, "artifacts", "ui-screenshots", "issue-861");
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "artifacts", "ui-screenshots", "issue-861");
    }

    /// <summary>One manifest row: PNG filename plus its one-sentence caption.</summary>
    private sealed record ManifestEntry(string File, string Caption);
}
