using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CAP.Avalonia.Views;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Renders the step-ordered visual walkthrough for the "Component Registry"
/// browser window (issue #656) as headless PNGs plus a <c>manifest.json</c> of
/// captions, written to <c>artifacts/ui-screenshots/issue-656/</c> for PR
/// review embedding.
/// </summary>
/// <remarks>
/// Run with: <c>dotnet test UnitTests/UnitTests.csproj --filter Category=UiWalkthrough</c>
/// </remarks>
[Trait("Category", "UiWalkthrough")]
// Renders the full registry browser window as Skia PNGs — CI-only (local runners
// exclude Category=Slow; run explicitly via --filter Category=UiWalkthrough).
[Trait("Category", "Slow")]
[Collection("LocalizationSingleton")]
public class RegistryBrowserWalkthroughTests
{
    private const int WindowWidth = 1100;
    private const int WindowHeight = 720;

    /// <summary>
    /// Pins English so the captured UI text is locale-independent (the
    /// walkthrough renders live <c>{loc:Localize}</c> bindings, which would
    /// otherwise follow the machine's OS language).
    /// </summary>
    public RegistryBrowserWalkthroughTests()
    {
        CAP.Avalonia.Services.Localization.LocalizationService.Instance.SetLanguage(
            CAP.Avalonia.Services.Localization.SupportedLanguage.English.Code);
    }

    /// <summary>
    /// Walks the registry browser flow — the freshly opened window with its
    /// loaded tile grid and filter bar, free-text filtering, process-mismatch
    /// flagging, and the selected-component detail column — capturing one
    /// screenshot per step.
    /// </summary>
    [AvaloniaFact]
    public void CaptureRegistryBrowserWalkthrough()
    {
        var outputDir = ResolveOutputDirectory();
        Directory.CreateDirectory(outputDir);
        foreach (var stale in Directory.GetFiles(outputDir, "*.png"))
            File.Delete(stale);

        var vm = MainViewModelTestHelper.CreateMainViewModel();
        var registry = vm.Registry;
        var manifest = new List<object>();

        var window = new RegistryBrowserWindow
        {
            Width = WindowWidth,
            Height = WindowHeight,
            DataContext = registry,
        };
        window.Show(); // Opened hook triggers the lazy index load (stubbed client, committed fixtures).
        PumpUntilComplete(registry.IndexLoadTask);
        // Tile previews load async after the grid (#771) — wait so every step
        // shows the fixture SVGs rendered in the tiles instead of placeholders.
        PumpUntilComplete(registry.PreviewsLoadTask);

        // Step 1: opened window — tile grid with all components, filter bar on top.
        Capture(window, outputDir, "01-loaded-grid.png");
        manifest.Add(new
        {
            file = "01-loaded-grid.png",
            caption = "Opening the Component Registry window loads the index and shows all components "
                + "as tiles with status chips and simulation tiers, under a search/process/status filter bar."
        });

        // Step 2: free-text search narrows the grid.
        registry.SearchText = "resonator";
        Capture(window, outputDir, "02-search-filter.png");
        manifest.Add(new
        {
            file = "02-search-filter.png",
            caption = "Typing in the search box filters the tiles by name and description."
        });

        // Step 3: a divergent active process flags foreign components.
        registry.SearchText = "";
        registry.ActiveProcessId = "my-inhouse-fab";
        Capture(window, outputDir, "03-process-mismatch.png");
        manifest.Add(new
        {
            file = "03-process-mismatch.png",
            caption = "Components targeting a process other than the design's active one are flagged "
                + "with a 'different process' chip."
        });

        // Step 4: selecting a tile loads its manifest into the detail column.
        registry.SelectedComponent = registry.Components.First(c => c.Id == "y-branch-1x2");
        PumpUntilComplete(registry.DetailsLoadTask);
        Capture(window, outputDir, "04-details.png");
        manifest.Add(new
        {
            file = "04-details.png",
            caption = "Selecting a component shows its description, ports, parameters, artifact "
                + "provenance, and license in the detail column."
        });

        window.Close();
        Dispatcher.UIThread.RunJobs();

        File.WriteAllText(
            Path.Combine(outputDir, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        foreach (var entry in Directory.GetFiles(outputDir, "*.png"))
            new FileInfo(entry).Length.ShouldBeGreaterThan(0, $"Screenshot must not be empty: {entry}");
        Directory.GetFiles(outputDir, "*.png").Length.ShouldBe(4);
    }

    /// <summary>Renders the current state of the shown window and saves it as a PNG.</summary>
    private static void Capture(Window window, string outputDir, string filename)
    {
        Dispatcher.UIThread.RunJobs();
        using var bitmap = window.CaptureRenderedFrame();
        bitmap.ShouldNotBeNull($"CaptureRenderedFrame returned null for {filename}");
        bitmap.Save(Path.Combine(outputDir, filename));
    }

    /// <summary>Pumps the headless dispatcher until the async ViewModel load completes.</summary>
    private static void PumpUntilComplete(Task task)
    {
        while (!task.IsCompleted)
            Dispatcher.UIThread.RunJobs();
        task.GetAwaiter().GetResult();
    }

    /// <summary>Walks up from the test binary to the repo root (directory containing a .sln).</summary>
    private static string ResolveOutputDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
                return Path.Combine(dir.FullName, "artifacts", "ui-screenshots", "issue-656");
            dir = dir.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "artifacts", "ui-screenshots", "issue-656");
    }
}
