using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Home;
using CAP.Avalonia.ViewModels.Onboarding.FirstStepsTutorial;
using CAP.Avalonia.Views;
using CAP.Avalonia.Views.Panels;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Visual walkthrough for issue #1080 (first-steps tutorial, slice 1) after the
/// merge with dev-ki: the Home screen's "Learn Lunima" entry point coexisting
/// with the curated example ladder (#1096), then the guided-tour card advancing
/// place → connect → simulate against a real canvas. Writes PNGs + manifest.json
/// to <c>artifacts/ui-screenshots/issue-1080/</c>.
/// </summary>
[Trait("Category", "UiScreenshots")]
public class Issue1080TutorialWalkthroughTests
{
    [AvaloniaFact]
    public async Task CaptureTutorialWalkthrough()
    {
        var outputDir = ResolveOutputDirectory();
        Directory.CreateDirectory(outputDir);

        CaptureHomeScreen(Path.Combine(outputDir, "01-home-tutorial-with-examples.png"));
        await CaptureTourSteps(outputDir);

        WriteManifest(outputDir);
    }

    /// <summary>
    /// 01 — the Home card with the green "Learn Lunima" button next to New/Open
    /// and the merged example ladder below it (real repo examples via the
    /// default walk-up discovery).
    /// </summary>
    private static void CaptureHomeScreen(string path)
    {
        var preferencesPath = Path.Combine(Path.GetTempPath(), $"walkthrough-1080-prefs-{Guid.NewGuid():N}.json");
        try
        {
            var preferences = new UserPreferencesService(preferencesPath);
            var home = new HomeViewModel(
                new RecentProjectsService(preferences),
                preferences,
                new ExampleDesignsService());

            var window = new Window
            {
                Width = 900,
                Height = 720,
                Background = new SolidColorBrush(Color.Parse("#1a1a1a")),
                Content = new HomeView { DataContext = home },
            };
            CaptureWindow(window, path);
        }
        finally
        {
            if (File.Exists(preferencesPath))
                File.Delete(preferencesPath);
        }
    }

    /// <summary>
    /// 02–04 — the tour card bound to a real engine observing a real canvas:
    /// starts at "Place a component", advances when a component is added, then
    /// again when two pins are connected.
    /// </summary>
    private static async Task CaptureTourSteps(string outputDir)
    {
        var canvas = new DesignCanvasViewModel();
        var tutorial = new TutorialViewModel(canvas);
        tutorial.Start();

        var window = new Window
        {
            Width = 420,
            Height = 240,
            Background = new SolidColorBrush(Color.Parse("#1a1a1a")),
            Content = new FirstStepsTutorialPanel { DataContext = tutorial },
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        SaveFrame(window, Path.Combine(outputDir, "02-tour-step1-place.png"));

        var startComp = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        startComp.WidthMicrometers = 250;
        startComp.HeightMicrometers = 250;
        var endComp = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        endComp.WidthMicrometers = 250;
        endComp.HeightMicrometers = 250;
        endComp.PhysicalX = 400;
        endComp.PhysicalY = 300;
        canvas.AddComponent(startComp);
        canvas.AddComponent(endComp);
        tutorial.CurrentStepIndex.ShouldBe(1, "placing a component must advance the tour");
        Dispatcher.UIThread.RunJobs();
        SaveFrame(window, Path.Combine(outputDir, "03-tour-step2-connect.png"));

        var startPin = startComp.PhysicalPins.First(p => p.Name == "out");
        var endPin = endComp.PhysicalPins.First(p => p.Name == "in");
        (await canvas.ConnectPinsAsync(startPin, endPin)).ShouldNotBeNull();
        tutorial.CurrentStepIndex.ShouldBe(2, "connecting two pins must advance the tour");
        Dispatcher.UIThread.RunJobs();
        SaveFrame(window, Path.Combine(outputDir, "04-tour-step3-simulate.png"));

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static void CaptureWindow(Window window, string path)
    {
        window.Show();
        Dispatcher.UIThread.RunJobs();
        SaveFrame(window, path);
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static void SaveFrame(Window window, string path)
    {
        var bitmap = window.CaptureRenderedFrame();
        bitmap.ShouldNotBeNull($"render miss for {Path.GetFileName(path)}");
        using (bitmap)
            ScreenshotArtifacts.SavePng(bitmap!, path).Length.ShouldBeGreaterThan(0);
    }

    private static void WriteManifest(string outputDir)
    {
        const string manifest = """
        [
          {"file": "01-home-tutorial-with-examples.png", "caption": "Home screen after the dev-ki merge: the green 'Learn Lunima' tour entry point coexists with the curated example ladder and its one-line descriptions from #1096."},
          {"file": "02-tour-step1-place.png", "caption": "Starting the tour shows the non-modal card at step 1/3, asking the user to place a component from the library."},
          {"file": "03-tour-step2-connect.png", "caption": "The engine observes the real canvas: adding a component auto-advances the card to step 2/3, connecting two pins."},
          {"file": "04-tour-step3-simulate.png", "caption": "Routing a waveguide between two pins advances to the final step 3/3, running the light simulation."}
        ]
        """;
        ScreenshotArtifacts.WriteText(Path.Combine(outputDir, "manifest.json"), manifest);
    }

    /// <summary>Repo-root <c>artifacts/ui-screenshots/issue-1080</c> (or <c>UI_SHOT_DIR/issue-1080</c>).</summary>
    private static string ResolveOutputDirectory()
    {
        var envDir = Environment.GetEnvironmentVariable("UI_SHOT_DIR");
        if (!string.IsNullOrEmpty(envDir))
            return Path.Combine(envDir, "issue-1080");

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
                return Path.Combine(dir.FullName, "artifacts", "ui-screenshots", "issue-1080");
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "artifacts", "ui-screenshots", "issue-1080");
    }
}
