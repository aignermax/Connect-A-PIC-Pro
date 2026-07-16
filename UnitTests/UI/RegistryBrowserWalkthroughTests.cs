using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CAP.Avalonia.Views.Panels;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Renders the step-ordered visual walkthrough for the Photonic Registry browser
/// panel (issue #656) as headless PNGs plus a <c>manifest.json</c> of captions,
/// written to <c>artifacts/ui-screenshots/issue-656/</c> for PR review embedding.
/// </summary>
/// <remarks>
/// Run with: <c>dotnet test UnitTests/UnitTests.csproj --filter Category=UiWalkthrough</c>
/// </remarks>
[Trait("Category", "UiWalkthrough")]
public class RegistryBrowserWalkthroughTests
{
    private const int PanelWidth = 450;

    /// <summary>
    /// Walks the registry browser flow — collapsed panel, loaded component list,
    /// process-mismatch flagging, and the selected-component detail pane —
    /// capturing one screenshot per step.
    /// </summary>
    [AvaloniaFact]
    public void CaptureRegistryBrowserWalkthrough()
    {
        var outputDir = ResolveOutputDirectory();
        Directory.CreateDirectory(outputDir);
        foreach (var stale in Directory.GetFiles(outputDir, "*.png"))
            File.Delete(stale);

        var vm = MainViewModelTestHelper.CreateMainViewModel();
        var registry = vm.RightPanel.Registry;
        var manifest = new List<object>();

        // Step 1: initial state — the panel sits collapsed in the right sidebar.
        Capture(vm, outputDir, "01-collapsed.png", 200);
        manifest.Add(new
        {
            file = "01-collapsed.png",
            caption = "The read-only Photonic Registry panel starts collapsed in the right sidebar."
        });

        // Step 2: expanding triggers the lazy index load (stubbed client, committed fixtures).
        registry.IsExpanded = true;
        PumpUntilComplete(registry.IndexLoadTask);
        Capture(vm, outputDir, "02-loaded-list.png", 620);
        manifest.Add(new
        {
            file = "02-loaded-list.png",
            caption = "Expanding the panel loads the registry index and lists components with status chips and simulation tiers."
        });

        // Step 3: a divergent active process flags foreign components.
        registry.ActiveProcessId = "my-inhouse-fab";
        Capture(vm, outputDir, "03-process-mismatch.png", 620);
        manifest.Add(new
        {
            file = "03-process-mismatch.png",
            caption = "Components targeting a process other than the design's active one are flagged with a 'different process' badge."
        });

        // Step 4: selecting a component loads its manifest into the detail pane.
        registry.SelectedComponent = registry.Components.First(c => c.Id == "y-branch-1x2");
        PumpUntilComplete(registry.DetailsLoadTask);
        Capture(vm, outputDir, "04-details.png", 950);
        manifest.Add(new
        {
            file = "04-details.png",
            caption = "Selecting a component shows its description, ports, parameters, artifact provenance, and license."
        });

        File.WriteAllText(
            Path.Combine(outputDir, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        foreach (var entry in Directory.GetFiles(outputDir, "*.png"))
            new FileInfo(entry).Length.ShouldBeGreaterThan(0, $"Screenshot must not be empty: {entry}");
        Directory.GetFiles(outputDir, "*.png").Length.ShouldBe(4);
    }

    /// <summary>Renders the registry panel at the given height and saves it as a PNG.</summary>
    private static void Capture(object dataContext, string outputDir, string filename, int height)
    {
        var window = new Window
        {
            Width = PanelWidth,
            Height = height,
            Content = new RegistryBrowserPanel { DataContext = dataContext }
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        using var bitmap = window.CaptureRenderedFrame();
        window.Close();
        Dispatcher.UIThread.RunJobs();

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
