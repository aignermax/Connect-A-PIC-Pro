using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Controls;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.GdsImport;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.Views;
using CAP.Avalonia.Views.Dialogs;
using CAP_DataAccess.Import.Gds;
using Shouldly;
using UnitTests.Helpers;
using UnitTests.Import.Gds;
using UnitTests.Services.GdsImport;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Visual walkthrough for issue #880 (opt-in auto-connect after GDS import):
/// the new dialog checkbox, the canvas with every facing pin pair routed by
/// the auto-connect pass, and an unroutable pair kept as a visible blocked
/// (red) path. Writes step-ordered PNGs and a <c>manifest.json</c> into
/// <c>artifacts/ui-screenshots/issue-880/</c>. Opt-in via <c>UI_SHOT_DIR</c>.
/// </summary>
[Trait("Category", "UiScreenshots")]
[Trait("Category", "Slow")]
[Collection("LocalizationSingleton")]
public class Issue880AutoConnectWalkthroughTests
{
    private const int CanvasWindowWidth = 900;
    private const int CanvasWindowHeight = 520;
    private const int CaptureAttempts = 3;

    /// <summary>Captures the three walkthrough states and writes the caption manifest.</summary>
    [AvaloniaFact]
    public async Task CaptureIssue880AutoConnectWalkthrough()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("UI_SHOT_DIR")))
            return;
        var dir = ResolveOutputDirectory();
        Directory.CreateDirectory(dir);
        foreach (var stale in Directory.GetFiles(dir, "*.png"))
            File.Delete(stale);

        var manifest = new List<ManifestEntry>();
        await CaptureDialogCheckbox(dir, manifest);
        await CaptureAutoConnectedCanvas(dir, manifest);
        await CaptureUnroutablePair(dir, manifest);

        ScreenshotArtifacts.WriteText(Path.Combine(dir, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            {
                WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));
        manifest.Count.ShouldBe(3);
    }

    /// <summary>The import dialog with the new opt-in checkbox ticked.</summary>
    private static async Task CaptureDialogCheckbox(string dir, List<ManifestEntry> manifest)
    {
        var root = Path.Combine(Path.GetTempPath(), "lunima-880-shot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var host = new GdsDesignScopeTestHost();
        try
        {
            var gdsPath = Path.Combine(root, "walkthrough.gds");
            File.WriteAllBytes(gdsPath, GdsTestWriter.Create()
                .StandardPrologue()
                .BeginCell("TOP").SRef("wg", 0, 0).SRef("wg", 40000, 0).EndCell()
                .BeginCell("wg")
                    .Boundary(1, 0, (0, 1750), (10000, 1750), (10000, 2250), (0, 2250), (0, 1750))
                    .Text(56, 0, "opt_in", 0, 2000)
                    .Text(56, 0, "opt_out", 10000, 2000)
                .EndCell()
                .EndLibrary()
                .ToArray());
            var executor = new GdsPlacementExecutor(
                new DesignCanvasViewModel(), new CommandManager(), () => new List<ComponentTemplate>());
            var vm = new GdsImportDialogViewModel(gdsPath, host.CreateService(), executor);
            await vm.StartAnalysisAsync();
            vm.AutoConnectAllPinsRequested = true;

            var dialog = new GdsImportDialog { DataContext = vm };
            dialog.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                Capture(dialog, dir, "01-dialog-auto-connect-checkbox.png",
                    "GDS import dialog: the new opt-in 'Auto-connect all pins after placement' "
                    + "checkbox (default OFF) below the existing re-route option.", manifest);
            }
            finally
            {
                dialog.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>Canvas after an import with auto-connect ON: every facing pair routed.</summary>
    private static async Task CaptureAutoConnectedCanvas(string dir, List<ManifestEntry> manifest)
    {
        var canvas = new DesignCanvasViewModel();
        var executor = new GdsPlacementExecutor(
            canvas, new CommandManager(), () => new[] { WalkthroughTemplates.Waveguide() });
        var plan = new GdsPlacementPlan
        {
            GroupName = "TOP",
            Placements = new[]
            {
                WalkthroughTemplates.Placement("wgA#0", 20, 60),
                WalkthroughTemplates.Placement("wgB#1", 160, 100),
                WalkthroughTemplates.Placement("wgC#2", 300, 60),
            },
        };

        var report = await executor.ExecuteAsync(plan, autoConnectAllPins: true);
        report.AutoConnectedCount.ShouldBe(2);
        report.AutoConnectFailedCount.ShouldBe(0);

        await CaptureCanvasWindow(canvas, dir, "02-auto-connected-pairs.png",
            "After import with auto-connect ON: both facing pin pairs are routed with the "
            + "normal direct-first/A*-fallback router — smooth S-bends across the offsets.",
            manifest);
    }

    /// <summary>An unroutable pair stays on the canvas as a blocked (red) path.</summary>
    private static async Task CaptureUnroutablePair(string dir, List<ManifestEntry> manifest)
    {
        var canvas = new DesignCanvasViewModel();
        var executor = new GdsPlacementExecutor(
            canvas, new CommandManager(),
            () => new[] { WalkthroughTemplates.Waveguide(), WalkthroughTemplates.Trap() });
        var plan = new GdsPlacementPlan
        {
            GroupName = "TOP",
            Placements = new[]
            {
                WalkthroughTemplates.Placement("trap#0", 60, 40, identifier: "trap"),
                WalkthroughTemplates.Placement("wgB#1", 300, 88),
            },
        };

        var report = await executor.ExecuteAsync(plan, autoConnectAllPins: true);
        report.AutoConnectFailedCount.ShouldBe(1);

        await CaptureCanvasWindow(canvas, dir, "03-unroutable-pair-reported.png",
            "A pair the router cannot route (target pin buried in a component body) is KEPT "
            + "on the canvas as a blocked path AND named in the import report — never silently red.",
            manifest);
    }

    /// <summary>Hosts the canvas in a MainView window and captures one frame.</summary>
    private static async Task CaptureCanvasWindow(
        DesignCanvasViewModel canvas, string dir, string filename, string caption,
        List<ManifestEntry> manifest)
    {
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
            await canvas.RecalculateRoutesAsync();
            Dispatcher.UIThread.RunJobs();
            foreach (var designCanvas in window.GetVisualDescendants().OfType<DesignCanvas>())
                designCanvas.InvalidateVisual();
            Dispatcher.UIThread.RunJobs();
            Capture(window, dir, filename, caption, manifest);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
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
                return Path.Combine(dir.FullName, "artifacts", "ui-screenshots", "issue-880");
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "artifacts", "ui-screenshots", "issue-880");
    }

    /// <summary>One manifest row: PNG filename plus its one-sentence caption.</summary>
    private sealed record ManifestEntry(string File, string Caption);
}
