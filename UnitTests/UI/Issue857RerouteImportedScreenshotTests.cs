using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CAP.Avalonia.Commands;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.Views.Panels;
using CAP_Core.Components.Core;
using CAP_Core.Routing;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Visual documentation for "Re-route imported (frozen) waveguide routes" (#857):
/// captures (1) the panel with frozen imported routes counted and both re-route buttons,
/// including the "hand-edited routes kept unchanged" note, and (2) the state after
/// "Re-route All" with the before/after length and bend delta report. PNGs +
/// manifest.json land in <c>artifacts/ui-screenshots/issue-857/</c>.
/// </summary>
[Trait("Category", "UiScreenshots")]
[Collection("LocalizationSingleton")]
public class Issue857RerouteImportedScreenshotTests
{
    private const int WindowWidth = 460;
    private const int WindowHeight = 320;
    private const int CaptureAttempts = 3;
    private const double DetourOffsetMicrometers = 800;

    /// <summary>Captures the frozen-routes state and the after-re-route delta state.</summary>
    [AvaloniaFact]
    public void CaptureRerouteImportedPanelStates()
    {
        // Opt-in like UiScreenshotTests: only runs when screenshots are explicitly requested.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("UI_SHOT_DIR")))
            return;
        var outputDir = ResolveOutputDirectory();
        Directory.CreateDirectory(outputDir);
        foreach (var stale in Directory.GetFiles(outputDir, "*.png"))
            File.Delete(stale);

        var canvas = new DesignCanvasViewModel();
        var commandManager = new CommandManager();
        var vm = MainViewModelTestHelper.CreateMainViewModel(
            commandManager: commandManager, canvas: canvas);

        SeedFrozenImportedConnection(canvas, 0);
        SeedFrozenImportedConnection(canvas, 1500);
        var edited = SeedFrozenImportedConnection(canvas, 3000);
        edited.Connection.BendRadiusOverrides[0] = 25;
        var reroute = vm.BottomPanel.RerouteImported;
        reroute.Refresh();

        var window = new Window
        {
            Width = WindowWidth,
            Height = WindowHeight,
            Content = new RerouteImportedPanel { DataContext = vm }
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            // State 1: two re-routable frozen imported routes + one hand-edited kept route.
            reroute.FrozenImportedCount.ShouldBe(2);
            reroute.HandEditedFrozenCount.ShouldBe(1);
            CaptureWithRetry(window, Path.Combine(outputDir, "01-frozen-imported-routes.png"));

            // State 2: after "Re-route All" — the delta report replaces the count and the
            // hand-edited route is still listed as kept unchanged.
            PumpUntilComplete(reroute.RerouteAllCommand.ExecuteAsync(null));
            reroute.FrozenImportedCount.ShouldBe(0);
            reroute.ResultText.ShouldNotBeNullOrEmpty();
            Dispatcher.UIThread.RunJobs();
            CaptureWithRetry(window, Path.Combine(outputDir, "02-reroute-delta.png"));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }

        WriteManifest(outputDir);
        Directory.GetFiles(outputDir, "*.png").Length.ShouldBe(2);
    }

    /// <summary>
    /// Adds a component pair and a frozen imported connection with a long U-detour dipping
    /// through the free gap between them — the suboptimal verbatim geometry a GDS import keeps.
    /// </summary>
    private static WaveguideConnectionViewModel SeedFrozenImportedConnection(
        DesignCanvasViewModel canvas, double offsetY)
    {
        var startComp = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        startComp.PhysicalX = 0;
        startComp.PhysicalY = offsetY;
        var endComp = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        endComp.PhysicalX = 400;
        endComp.PhysicalY = offsetY;
        canvas.AddComponent(startComp);
        canvas.AddComponent(endComp);

        var startPin = startComp.PhysicalPins.First(p => p.Name == "out");
        var endPin = endComp.PhysicalPins.First(p => p.Name == "in");
        var (sx, sy) = startPin.GetAbsolutePosition();
        var (ex, ey) = endPin.GetAbsolutePosition();
        double downX = sx + (ex - sx) / 3;
        double upX = sx + 2 * (ex - sx) / 3;
        double detourY = sy + DetourOffsetMicrometers;

        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(sx, sy, downX, sy, 0));
        path.Segments.Add(new StraightSegment(downX, sy, downX, detourY, 90));
        path.Segments.Add(new StraightSegment(downX, detourY, upX, detourY, 0));
        path.Segments.Add(new StraightSegment(upX, detourY, upX, sy, 270));
        path.Segments.Add(new StraightSegment(upX, sy, ex, ey, 0));

        var connVm = canvas.ConnectPinsWithCachedRoute(startPin, endPin, path);
        connVm.ShouldNotBeNull();
        connVm!.Connection.IsRouteFrozen = true;
        return connVm;
    }

    /// <summary>
    /// Pumps the headless UI dispatcher until <paramref name="task"/> completes,
    /// so the async re-route pass finishes without deadlocking the test thread.
    /// </summary>
    private static void PumpUntilComplete(Task task)
    {
        while (!task.IsCompleted)
            Dispatcher.UIThread.RunJobs();
        task.GetAwaiter().GetResult();
    }

    private static void WriteManifest(string outputDir)
    {
        const string manifest = """
        [
          {"file": "01-frozen-imported-routes.png", "caption": "Imported Routes section (auto-appears in the routing panel when a GDS import left frozen routes): 2 re-routable frozen routes counted, Re-route All / Re-route Selected buttons, and 1 hand-edited frozen route explicitly listed as kept unchanged."},
          {"file": "02-reroute-delta.png", "caption": "After Re-route All (one undoable command): the green report shows the before/after total length and equivalent-90-degree-bend delta of the 2 re-routed routes; the hand-edited route was never touched and stays noted as kept."}
        ]
        """;
        ScreenshotArtifacts.WriteText(Path.Combine(outputDir, "manifest.json"), manifest);
    }

    /// <summary>Repo-root <c>artifacts/ui-screenshots/issue-857</c> (or <c>UI_SHOT_DIR/issue-857</c>).</summary>
    private static string ResolveOutputDirectory()
    {
        var envDir = Environment.GetEnvironmentVariable("UI_SHOT_DIR");
        if (!string.IsNullOrEmpty(envDir))
            return Path.Combine(envDir, "issue-857");

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
                return Path.Combine(dir.FullName, "artifacts", "ui-screenshots", "issue-857");
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "artifacts", "ui-screenshots", "issue-857");
    }

    /// <summary>
    /// Captures the window, pumping the dispatcher before every attempt and keeping the
    /// last successful frame (headless rendering can miss frames).
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
