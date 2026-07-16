using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CAP.Avalonia.Views.Panels;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Visual walkthrough for issue #695 (compact Transient / Eye-BER tab headers in the analysis
/// dock): renders the dock's UI flow as step-ordered headless PNGs into
/// <c>artifacts/ui-screenshots/issue-695/</c> plus a <c>manifest.json</c> with one-sentence
/// captions. Uses the same Skia headless harness as <see cref="UiScreenshotTests"/>.
/// </summary>
[Trait("Category", "UiScreenshots")]
public class Issue695WalkthroughScreenshotTests
{
    private const int MinDistinctSampledColors = 10;
    private const int MinDistinctColorsSparseFrame = 4;
    private const int SampleGridSize = 64;

    /// <summary>Renders the three walkthrough steps and writes the manifest.</summary>
    [AvaloniaFact]
    public void CaptureIssue695Walkthrough()
    {
        var dir = ResolveOutputDirectory();
        Directory.CreateDirectory(dir);
        foreach (var stale in Directory.GetFiles(dir, "*.png"))
            File.Delete(stale);

        var manifest = new List<ManifestEntry>();
        var vm = MainViewModelTestHelper.CreateMainViewModel();

        // Window height = header (26) + resize grip (5) + dock content (260) + margin.
        var window = new Window
        {
            Width = 900,
            Height = 300,
            Content = new AnalysisDockPanel { DataContext = vm }
        };
        window.Show();
        PumpRenderLoop();

        // The collapsed frame is legitimately sparse (a single 26 px header bar on an empty
        // window), so it gets a lower distinct-color floor than the expanded frames.
        Capture(window, dir, "01-dock-collapsed.png",
            "The analysis dock starts collapsed to its 26 px header bar above the error console.",
            manifest, minDistinctColors: MinDistinctColorsSparseFrame);

        vm.BottomPanel.Analysis.IsVisible = true;
        vm.BottomPanel.Analysis.SelectedTabIndex = 0;
        PumpRenderLoop();

        Capture(window, dir, "02-transient-tab-compact-headers.png",
            "Expanding the dock shows the Transient tab with the new compact 26 px / 12 px tab "
            + "strip, leaving the reclaimed vertical space to the plot area.", manifest);

        vm.BottomPanel.Analysis.SelectedTabIndex = 1;
        PumpRenderLoop();

        Capture(window, dir, "03-eye-ber-tab-compact-headers.png",
            "The Eye / BER tab uses the same compact headers — the style is scoped to this "
            + "TabControl only, so no other tabs in the app change.", manifest);

        window.Close();
        Dispatcher.UIThread.RunJobs();

        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        File.WriteAllText(Path.Combine(dir, "manifest.json"), json);

        manifest.Count.ShouldBe(3);
    }

    /// <summary>
    /// Pumps the headless render timer and dispatcher a few times so render-loop-driven
    /// controls (e.g. OxyPlot views inside the tabs) actually paint before capture.
    /// </summary>
    private static void PumpRenderLoop()
    {
        for (int i = 0; i < 5; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Captures the shown window to a PNG, fails on a near-blank frame, records the caption.</summary>
    private static void Capture(
        Window window, string dir, string filename, string caption, List<ManifestEntry> manifest,
        int minDistinctColors = MinDistinctSampledColors)
    {
        var bitmap = window.CaptureRenderedFrame();
        bitmap.ShouldNotBeNull($"CaptureRenderedFrame returned null for {filename}");

        var path = Path.Combine(dir, filename);
        int distinctColors;
        using (bitmap)
        {
            distinctColors = CountDistinctSampledColors(bitmap);
            bitmap.Save(path);
        }

        distinctColors.ShouldBeGreaterThan(minDistinctColors,
            $"Near-blank render — only {distinctColors} distinct sampled colors in {filename}.");
        manifest.Add(new ManifestEntry(filename, caption));
    }

    /// <summary>Samples a grid of pixels and counts distinct ARGB values (blank-frame guard).</summary>
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

    /// <summary>Repo-root walkthrough output directory (env override: <c>UI_SHOT_DIR</c>).</summary>
    private static string ResolveOutputDirectory()
    {
        var envDir = Environment.GetEnvironmentVariable("UI_SHOT_DIR");
        if (!string.IsNullOrEmpty(envDir))
            return Path.Combine(envDir, "issue-695");

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
                return Path.Combine(dir.FullName, "artifacts", "ui-screenshots", "issue-695");
            dir = dir.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "artifacts", "ui-screenshots", "issue-695");
    }

    /// <summary>One manifest row: PNG file name plus its one-sentence caption.</summary>
    private sealed record ManifestEntry(string File, string Caption);
}
