using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CAP.Avalonia.ViewModels.Analysis.CircuitOptimization;
using CAP.Avalonia.Views.Panels;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Visual walkthrough for issue #820 (circuit optimization search): renders the
/// <see cref="CircuitOptimizationPanel"/> in its three key states — configured and
/// ready, running with progress, and showing the ranked top-N variant list with an
/// applied variant. Writes numbered PNGs + manifest.json to
/// <c>artifacts/ui-screenshots/issue-820/</c>.
/// </summary>
[Trait("Category", "UiScreenshots")]
[Collection("LocalizationSingleton")]
public class Issue820CircuitOptimizationScreenshotTests
{
    private const int PanelWidth = 420;
    private const int PanelHeight = 640;

    [AvaloniaFact]
    public void CaptureCircuitOptimizationWalkthrough()
    {
        // Opt-in: heavy headless render, only on explicit request (see UiScreenshotTests).
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("UI_SHOT_DIR")))
            return;
        var outputDir = ResolveOutputDirectory();
        Directory.CreateDirectory(outputDir);

        var vm = MainViewModelTestHelper.CreateMainViewModel();
        var optimization = vm.RightPanel.Optimization;
        SeedReadyState(optimization);
        Capture(vm, outputDir, "01-panel-ready.png");

        SeedRunningState(optimization);
        Capture(vm, outputDir, "02-running.png");

        SeedResultsState(optimization);
        Capture(vm, outputDir, "03-results-applied.png");

        WriteManifest(outputDir);
    }

    /// <summary>Configured panel: two targets, default budget/seed, run button enabled.</summary>
    private static void SeedReadyState(CircuitOptimizationViewModel optimization)
    {
        optimization.HasParameters = true;
        optimization.Targets.Add(new OptimizationTargetOption(
            "Total power at all outputs", new[] { Guid.NewGuid(), Guid.NewGuid() }));
        optimization.Targets.Add(new OptimizationTargetOption(
            "GC_out.o1", new[] { Guid.NewGuid() }));
        optimization.SelectedTarget = optimization.Targets[0];
    }

    /// <summary>Mid-run: cancel button visible, live progress in the status line.</summary>
    private static void SeedRunningState(CircuitOptimizationViewModel optimization)
    {
        optimization.IsOptimizing = true;
        optimization.StatusText = "Evaluation 23/50 — best score 0.7141";
    }

    /// <summary>Finished run: baseline + three ranked variants, best one applied.</summary>
    private static void SeedResultsState(CircuitOptimizationViewModel optimization)
    {
        optimization.IsOptimizing = false;
        optimization.StatusText = "Done — 50 evaluations, 3 improved variant(s).";
        optimization.BaselineText = "Baseline score: 0.0955";

        optimization.Variants.Add(MakeVariant(1, "0.9987", "+0.9032",
            "DC1 · Coupling = 0.499   PS1 · Phase = 0.248"));
        optimization.Variants.Add(MakeVariant(2, "0.9421", "+0.8466",
            "DC1 · Coupling = 0.421   PS1 · Phase = 0.239"));
        optimization.Variants.Add(MakeVariant(3, "0.8710", "+0.7755",
            "DC1 · Coupling = 0.377   PS1 · Phase = 0.301"));
        optimization.Variants[0].IsApplied = true;
    }

    private static OptimizationVariantViewModel MakeVariant(
        int rank, string score, string improvement, string summary) =>
        new(rank, score, improvement, summary, new[] { 0.5, 0.25 }, _ => { });

    private static void Capture(object dataContext, string outputDir, string filename)
    {
        var window = new Window
        {
            Width = PanelWidth,
            Height = PanelHeight,
            Content = new CircuitOptimizationPanel { DataContext = dataContext },
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var bitmap = window.CaptureRenderedFrame();
        window.Close();
        Dispatcher.UIThread.RunJobs();

        bitmap.ShouldNotBeNull($"render miss for {filename}");
        byte[] bytes;
        using (bitmap)
            bytes = ScreenshotArtifacts.SavePng(bitmap!, Path.Combine(outputDir, filename));
        bytes.Length.ShouldBeGreaterThan(0);
    }

    private static void WriteManifest(string outputDir)
    {
        const string manifest = """
        [
          {"file": "01-panel-ready.png", "caption": "Optimization panel ready: target selector (total output power / single coupler port), maximize toggle, evaluation budget and seed, and the Run button."},
          {"file": "02-running.png", "caption": "Run in progress: Run disabled, Cancel visible, live status line shows evaluation 23/50 and the best score so far — cancellation is clean at any point."},
          {"file": "03-results-applied.png", "caption": "Finished run: baseline score plus ranked top-N variants with score, improvement over baseline and the parameter assignment; variant #1 applied via the undo-safe one-click Apply (Ctrl+Z restores)."}
        ]
        """;
        ScreenshotArtifacts.WriteText(Path.Combine(outputDir, "manifest.json"), manifest);
    }

    /// <summary>Repo-root <c>artifacts/ui-screenshots/issue-820</c> (or <c>UI_SHOT_DIR/issue-820</c>).</summary>
    private static string ResolveOutputDirectory()
    {
        var envDir = Environment.GetEnvironmentVariable("UI_SHOT_DIR");
        if (!string.IsNullOrEmpty(envDir))
            return Path.Combine(envDir, "issue-820");

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
                return Path.Combine(dir.FullName, "artifacts", "ui-screenshots", "issue-820");
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "artifacts", "ui-screenshots", "issue-820");
    }
}
