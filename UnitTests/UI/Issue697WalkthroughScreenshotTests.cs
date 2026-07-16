using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CAP.Avalonia.Services.DialogSizing;
using CAP.Avalonia.Views;
using Shouldly;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Visual walkthrough for issue #697 (small dialogs rendering collapsed on every other opening
/// on Linux/X11): renders the real <see cref="RenameDialog"/> in its intended state, in the
/// collapsed state the X11 race produced, and after <see cref="DialogSizeGuard"/> restores it.
/// Writes step-ordered PNGs plus a <c>manifest.json</c> with one-sentence captions into
/// <c>artifacts/ui-screenshots/issue-697/</c>, using the same Skia headless harness as
/// <see cref="UiScreenshotTests"/>.
/// </summary>
[Trait("Category", "UiScreenshots")]
public class Issue697WalkthroughScreenshotTests
{
    private const int MinDistinctSampledColors = 10;
    private const int SampleGridSize = 64;

    /// <summary>RenameDialog's declared size in AXAML (Width="340" Height="130").</summary>
    private const double RequestedWidth = 340;
    private const double RequestedHeight = 130;

    /// <summary>Collapsed size mimicking the late X11 ConfigureNotify shrinking the dialog.</summary>
    private const double CollapsedWidth = 200;
    private const double CollapsedHeight = 40;

    /// <summary>Renders the three walkthrough steps and writes the manifest.</summary>
    [AvaloniaFact]
    public void CaptureIssue697Walkthrough()
    {
        var dir = ResolveOutputDirectory();
        Directory.CreateDirectory(dir);
        foreach (var stale in Directory.GetFiles(dir, "*.png"))
            File.Delete(stale);

        var manifest = new List<ManifestEntry>();

        var dialog = new RenameDialog("MZI Group");
        dialog.Show();
        PumpRenderLoop();

        Capture(dialog, dir, "01-dialog-requested-size.png",
            "The Rename dialog as it should always appear: its full requested 340x130 size with "
            + "prompt, text box and buttons visible.", manifest);

        // Simulate the Linux/X11 bug (AvaloniaUI/Avalonia#12581): a late ConfigureNotify during
        // the show handshake collapses the window on every other opening.
        dialog.Width = CollapsedWidth;
        dialog.Height = CollapsedHeight;
        PumpRenderLoop();
        dialog.ClientSize.Height.ShouldBeLessThan(RequestedHeight);

        Capture(dialog, dir, "02-dialog-collapsed-bug.png",
            "Without the guard, the X11 race collapses the same dialog on every other opening, "
            + "clipping the text box and buttons away.", manifest);

        // The fix: DialogSizeGuard (installed app-wide in App.axaml.cs) detects the collapse
        // shortly after Opened and re-applies the size requested at show time.
        DialogSizeGuard.EnforceRequestedSize(dialog, RequestedWidth, RequestedHeight, SizeToContent.Manual);
        PumpRenderLoop();
        dialog.Width.ShouldBe(RequestedWidth);
        dialog.Height.ShouldBe(RequestedHeight);

        Capture(dialog, dir, "03-dialog-restored-by-guard.png",
            "DialogSizeGuard detects the collapse right after opening and restores the requested "
            + "size, so the dialog is fully usable again.", manifest);

        dialog.Close();
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
    /// Pumps the headless render timer and dispatcher a few times so size changes and
    /// layout actually reach the rendered frame before capture.
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
        Window window, string dir, string filename, string caption, List<ManifestEntry> manifest)
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

        distinctColors.ShouldBeGreaterThan(MinDistinctSampledColors,
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
            return Path.Combine(envDir, "issue-697");

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
                return Path.Combine(dir.FullName, "artifacts", "ui-screenshots", "issue-697");
            dir = dir.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "artifacts", "ui-screenshots", "issue-697");
    }

    /// <summary>One walkthrough step in the manifest embedded into the PR comment.</summary>
    private sealed record ManifestEntry(string File, string Caption);
}
