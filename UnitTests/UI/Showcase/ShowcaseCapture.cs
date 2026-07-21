using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CAP.Avalonia.Services.Localization;
using Shouldly;

namespace UnitTests.UI.Showcase;

/// <summary>
/// Shared capture plumbing for the v0.12 feature-showcase screenshots: opt-in gating via
/// <c>UI_SHOT_DIR</c>, render-loop pumping, retry-capture with a blank-frame guard, and
/// crop/compose helpers (all Avalonia-native, so they work on every OS).
/// </summary>
internal static class ShowcaseCapture
{
    private const int MinDistinctSampledColors = 12;
    private const int SampleGridSize = 64;
    private const int CaptureAttempts = 3;

    /// <summary>True when showcase rendering was explicitly requested (opt-in like the walkthroughs).</summary>
    public static bool Enabled =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("UI_SHOT_DIR"));

    /// <summary>Output directory <c>UI_SHOT_DIR/v0.12</c> (or repo-root <c>artifacts/ui-screenshots/v0.12</c>).</summary>
    public static string OutputDirectory()
    {
        var envDir = Environment.GetEnvironmentVariable("UI_SHOT_DIR");
        var root = !string.IsNullOrEmpty(envDir)
            ? Path.Combine(envDir, "v0.12")
            : Path.Combine(FindRepoRoot(), "artifacts", "ui-screenshots", "v0.12");
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>
    /// Runs <paramref name="body"/> with the UI language pinned to English AND the thread
    /// culture pinned to en-US (so number formatting in canvas labels, spinners and status
    /// texts renders as "0.5", not a locale-specific "0,5"); both are restored afterwards.
    /// </summary>
    public static async Task WithEnglishUiAsync(Func<Task> body)
    {
        var previousLanguage = LocalizationService.Instance.ActiveLanguageCode;
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        var previousDefault = CultureInfo.DefaultThreadCurrentCulture;
        var previousDefaultUi = CultureInfo.DefaultThreadCurrentUICulture;
        LocalizationService.Instance.SetLanguage("en");
        var english = new CultureInfo("en-US");
        CultureInfo.CurrentCulture = english;
        CultureInfo.CurrentUICulture = english;
        // Dispatcher jobs run on execution contexts captured elsewhere, which do NOT carry
        // the async-flowed CurrentCulture — the process-wide defaults cover those too.
        CultureInfo.DefaultThreadCurrentCulture = english;
        CultureInfo.DefaultThreadCurrentUICulture = english;
        try
        {
            await body();
        }
        finally
        {
            LocalizationService.Instance.SetLanguage(previousLanguage);
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
            CultureInfo.DefaultThreadCurrentCulture = previousDefault;
            CultureInfo.DefaultThreadCurrentUICulture = previousDefaultUi;
        }
    }

    /// <summary>Pumps dispatcher jobs and forces render-timer ticks so render-loop-driven
    /// controls (OxyPlot plots, canvas repaints) actually paint before capture.</summary>
    public static void PumpRenderLoop(int ticks = 6)
    {
        for (int i = 0; i < ticks; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Captures the shown window (with retries against headless frame misses),
    /// guards against near-blank frames and writes the PNG atomically.</summary>
    public static void CaptureWindow(Window window, string path)
    {
        using var bitmap = CaptureFrame(window, Path.GetFileName(path));
        ScreenshotArtifacts.SavePng(bitmap, path);
    }

    /// <summary>Captures the shown window into a bitmap (no file) — for composed motifs.
    /// Caller owns the returned bitmap.</summary>
    public static WriteableBitmap CaptureFrame(Window window, string label)
    {
        WriteableBitmap? bitmap = null;
        for (int attempt = 0; attempt < CaptureAttempts; attempt++)
        {
            PumpRenderLoop();
            var frame = window.CaptureRenderedFrame();
            if (frame == null) continue;
            bitmap?.Dispose();
            bitmap = frame;
        }

        bitmap.ShouldNotBeNull($"CaptureRenderedFrame stayed null for {label}");
        CountDistinctSampledColors(bitmap!).ShouldBeGreaterThan(MinDistinctSampledColors,
            $"Near-blank render for {label}");
        return bitmap!;
    }

    /// <summary>Composes crops of already-captured bitmaps side by side (thin dark gutter)
    /// into one PNG — used for the PDK-management and multi-language motifs.</summary>
    public static void ComposeSideBySide(
        string path, IReadOnlyList<(Bitmap Source, PixelRect Crop)> panes, int gutter = 6)
    {
        int width = panes.Sum(p => p.Crop.Width) + gutter * (panes.Count - 1);
        int height = panes.Max(p => p.Crop.Height);

        using var target = new RenderTargetBitmap(new PixelSize(width, height));
        using (var ctx = target.CreateDrawingContext())
        {
            ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(24, 24, 24)),
                new Rect(0, 0, width, height));
            double x = 0;
            foreach (var (source, crop) in panes)
            {
                ctx.DrawImage(source,
                    new Rect(crop.X, crop.Y, crop.Width, crop.Height),
                    new Rect(x, 0, crop.Width, crop.Height));
                x += crop.Width + gutter;
            }
        }
        using var stream = new MemoryStream();
        target.Save(stream);
        ScreenshotArtifacts.WriteBytes(path, stream.ToArray());
    }

    /// <summary>Window-space pixel rect of <paramref name="control"/> inside <paramref name="window"/>.</summary>
    public static PixelRect BoundsIn(Window window, Visual control)
    {
        var topLeft = control.TranslatePoint(new Point(0, 0), window)!.Value;
        return new PixelRect((int)topLeft.X, (int)topLeft.Y,
            (int)control.Bounds.Width, (int)control.Bounds.Height);
    }

    /// <summary>Samples a pixel grid and counts distinct ARGB values (blank-frame guard).</summary>
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

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0) return dir.FullName;
            dir = dir.Parent;
        }
        return AppContext.BaseDirectory;
    }
}
