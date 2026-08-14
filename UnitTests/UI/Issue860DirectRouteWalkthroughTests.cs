using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CAP.Avalonia.Controls;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Settings;
using CAP.Avalonia.Views;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Visual walkthrough for issue #860 (direct/S-bend-first routing policy): renders the real
/// design canvas with one Auto connection in three states — the new direct S-bend route on a
/// clear line, the old A* grid route with the policy disabled, and the automatic A* fallback
/// when an obstacle blocks the styled path — plus the new Settings → Routing toggle. Writes
/// step-ordered PNGs and a <c>manifest.json</c> into
/// <c>artifacts/ui-screenshots/issue-860/</c>. Opt-in via <c>UI_SHOT_DIR</c>.
/// </summary>
[Trait("Category", "UiScreenshots")]
// Heavy Skia frame captures — CI covers it, local default runs exclude Category=Slow.
[Trait("Category", "Slow")]
[Collection("LocalizationSingleton")]
public class Issue860DirectRouteWalkthroughTests
{
    private const int CanvasWindowWidth = 900;
    private const int CanvasWindowHeight = 520;
    private const int CaptureAttempts = 3;
    private const double StartComponentX = 40;
    private const double StartComponentY = 40;
    private const double EndComponentX = 490;
    private const double EndComponentY = 120;
    private const double BlockerX = 350;
    private const double BlockerY = 80;
    private const double BlockerWidth = 60;
    private const double BlockerHeight = 220;

    /// <summary>Captures the four walkthrough states and writes the caption manifest.</summary>
    [AvaloniaFact]
    public async Task CaptureIssue860DirectRouteWalkthrough()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("UI_SHOT_DIR")))
            return;
        var dir = ResolveOutputDirectory();
        Directory.CreateDirectory(dir);
        foreach (var stale in Directory.GetFiles(dir, "*.png"))
            File.Delete(stale);

        var manifest = new List<ManifestEntry>();
        await CaptureCanvasScenes(dir, manifest);
        CaptureSettingsToggle(dir, manifest);

        ScreenshotArtifacts.WriteText(Path.Combine(dir, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            {
                WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));
        manifest.Count.ShouldBe(4);
    }

    /// <summary>Builds one two-component design and captures it in the three routing states.</summary>
    private static async Task CaptureCanvasScenes(string dir, List<ManifestEntry> manifest)
    {
        var canvas = new DesignCanvasViewModel();
        var startComp = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        startComp.PhysicalX = StartComponentX;
        startComp.PhysicalY = StartComponentY;
        var endComp = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        endComp.PhysicalX = EndComponentX;
        endComp.PhysicalY = EndComponentY;
        canvas.AddComponent(startComp);
        canvas.AddComponent(endComp);

        var startPin = startComp.PhysicalPins.First(p => p.Name == "out");
        var endPin = endComp.PhysicalPins.First(p => p.Name == "in");
        var connVm = await canvas.ConnectPinsAsync(startPin, endPin);
        connVm.ShouldNotBeNull();

        var vm = MainViewModelTestHelper.CreateMainViewModel(canvas: canvas);
        var window = new Window
        {
            Width = CanvasWindowWidth,
            Height = CanvasWindowHeight,
            Content = new MainView { DataContext = vm },
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            connVm!.Connection.RoutedPath!.IsDirectStyledRoute.ShouldBeTrue(
                "a clear line must get the direct styled route by default");
            RefreshCanvas(window);
            Capture(window, dir, "01-direct-sbend-default.png",
                "Default policy on a clear line: the Auto connection gets the smooth direct "
                + "S-bend immediately — no grid staircase, no A* run.", manifest);

            canvas.PreferDirectStyledRoutes = false;
            await Reroute(canvas, connVm);
            connVm.Connection.RoutedPath!.IsDirectStyledRoute.ShouldBeFalse(
                "with the policy off every route must come from the grid search");
            RefreshCanvas(window);
            Capture(window, dir, "02-policy-disabled-astar.png",
                "Same layout with the new Settings toggle off: the connection is routed by "
                + "the classic A* grid search instead of the direct styled geometry.", manifest);

            canvas.PreferDirectStyledRoutes = true;
            var blocker = TestComponentFactory.CreateStraightWaveGuide();
            blocker.WidthMicrometers = BlockerWidth;
            blocker.HeightMicrometers = BlockerHeight;
            blocker.PhysicalX = BlockerX;
            blocker.PhysicalY = BlockerY;
            canvas.AddComponent(blocker);
            await Reroute(canvas, connVm);
            connVm.Connection.RoutedPath!.IsDirectStyledRoute.ShouldBeFalse(
                "an obstacle across the styled path must trigger the A* fallback");
            RefreshCanvas(window);
            Capture(window, dir, "03-obstacle-astar-fallback.png",
                "A component dropped across the styled path: the direct candidate is rejected "
                + "against the obstacle grid and A* automatically routes around it.", manifest);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Renders the Settings → Routing page with the new direct-first checkbox.</summary>
    private static void CaptureSettingsToggle(string dir, List<ManifestEntry> manifest)
    {
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);
        var settingsVm = new SettingsWindowViewModel(new ISettingsPage[]
        {
            new RoutingSettingsPage(new DesignCanvasViewModel(), LocalizationService.Instance),
        });
        var window = new SettingsWindow { DataContext = settingsVm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Capture(window, dir, "04-settings-routing-toggle.png",
                "Settings → Routing: the new 'Direct routes first' escape hatch sits above the "
                + "diagonal option, on by default, with a description of the A*-fallback "
                + "behavior.", manifest);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Invalidates the connection and awaits a full recalculation.</summary>
    private static async Task Reroute(
        DesignCanvasViewModel canvas, WaveguideConnectionViewModel connVm)
    {
        connVm.Connection.InvalidateRoute();
        await canvas.RecalculateRoutesAsync();
    }

    /// <summary>Drains pending repaint jobs and forces a fresh render of the final route.</summary>
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
                return Path.Combine(dir.FullName, "artifacts", "ui-screenshots", "issue-860");
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "artifacts", "ui-screenshots", "issue-860");
    }

    /// <summary>One manifest row: PNG filename plus its one-sentence caption.</summary>
    private sealed record ManifestEntry(string File, string Caption);
}
