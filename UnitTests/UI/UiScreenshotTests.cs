using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CAP.Avalonia.Views;
using CAP.Avalonia.Views.Panels;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Headless screenshot harness — renders key Avalonia Views offscreen via Skia and writes PNGs
/// to <c>artifacts/ui-screenshots/</c> in the repo root for downstream QA visual inspection.
/// </summary>
/// <remarks>
/// Run with: <c>dotnet test UnitTests/UnitTests.csproj --filter Category=UiScreenshots</c>
/// Output directory override: set env var <c>UI_SHOT_DIR</c> to an absolute path.
/// </remarks>
[Trait("Category", "UiScreenshots")]
public class UiScreenshotTests
{
    // A solid-color blank frame samples to 1 distinct color; anti-aliased edges may add a
    // handful. A real rendered UI yields dozens-to-hundreds. 10 is the fail-fast floor.
    private const int MinDistinctSampledColors = 10;

    /// <summary>
    /// Captures all target Views in one pass. Uses a single MainViewModel so panel bindings
    /// that navigate through RightPanel/LeftPanel sub-properties resolve correctly.
    /// Each panel is wrapped in its own Window; per-panel construction failures are caught
    /// and logged so one bad panel does not block the rest, but a blank/near-blank render
    /// FAILS the test loudly (false confidence is worse than no image).
    /// </summary>
    [AvaloniaFact]
    public void CaptureAllUiScreenshots()
    {
        var outputDir = ResolveOutputDirectory();
        ClearStalePngs(outputDir);
        Directory.CreateDirectory(outputDir);

        var vm = MainViewModelTestHelper.CreateMainViewModel();
        var captured = new List<(string Path, int DistinctColors)>();
        var skipped = new List<(string Name, string Reason)>();

        // All panels use x:DataType="vm:MainViewModel", so pass the full VM as DataContext.
        TryCapture(() => new MainView(), vm, 1280, 900, outputDir, "MainView.png", captured, skipped);
        TryCapture(() => new DesignChecksPanel(), vm, 450, 700, outputDir, "DesignChecksPanel.png", captured, skipped);
        TryCapture(() => new AiAssistantPanel(), vm, 450, 800, outputDir, "AiAssistantPanel.png", captured, skipped);
        TryCapture(() => new LayoutCompressionPanel(), vm, 450, 600, outputDir, "LayoutCompressionPanel.png", captured, skipped);
        TryCapture(() => new RoutingDiagnosticsPanel(), vm, 600, 700, outputDir, "RoutingDiagnosticsPanel.png", captured, skipped);
        TryCapture(() => new SelectedComponentPropertiesPanel(), vm, 450, 600, outputDir, "SelectedComponentPropertiesPanel.png", captured, skipped);

        // Settings content: the environment manager moved from the Properties sidebar to a
        // Settings page — captured standalone with its own ViewModel (not part of MainViewModel).
        var envVm = new CAP.Avalonia.ViewModels.Export.PythonEnvironmentManager.PythonEnvironmentManagerViewModel(
            new CAP_Core.Export.PythonEnvironmentManager.PythonEnvironmentRegistry(
                Path.Combine(Path.GetTempPath(), $"lunima-ui-shot-registry-{Guid.NewGuid():N}.json")),
            new CAP_Core.Export.PythonEnvironmentManager.UvBootstrapper(),
            new CAP_Core.Export.PythonEnvironmentManager.NazcaPackageInstaller(),
            new CAP_Core.Export.PythonEnvironmentManager.EnvironmentHealthChecker(
                new CAP_Core.Export.PythonDiscoveryService()));
        TryCapture(() => new PythonEnvironmentManagerPanel(), envVm, 500, 700, outputDir, "PythonEnvironmentManagerPanel.png", captured, skipped);

        foreach (var (name, reason) in skipped)
            Console.WriteLine($"[SKIPPED] {name}: {reason}");

        foreach (var (path, colors) in captured)
            Console.WriteLine($"[OK] {path} ({new FileInfo(path).Length:N0} bytes, {colors} distinct sampled colors)");

        foreach (var (path, colors) in captured)
        {
            new FileInfo(path).Exists.ShouldBeTrue($"Screenshot file must exist: {path}");
            colors.ShouldBeGreaterThan(MinDistinctSampledColors,
                $"Near-blank render — only {colors} distinct sampled colors in {path}. " +
                "Likely UseSkia() is missing or UseHeadlessDrawing != false.");
        }

        captured.Count.ShouldBeGreaterThan(0, "At least one screenshot must be captured");
    }

    private static void TryCapture(
        Func<Control> createView,
        object dataContext,
        int width,
        int height,
        string outputDir,
        string filename,
        List<(string Path, int DistinctColors)> captured,
        List<(string Name, string Reason)> skipped)
    {
        try
        {
            var view = createView();
            view.DataContext = dataContext;

            var window = new Window
            {
                Width = width,
                Height = height,
                Content = view
            };

            window.Show();
            Dispatcher.UIThread.RunJobs();

            var bitmap = window.CaptureRenderedFrame();
            window.Close();
            Dispatcher.UIThread.RunJobs();

            if (bitmap == null)
            {
                skipped.Add((filename, "CaptureRenderedFrame returned null — likely a render miss"));
                return;
            }

            var path = Path.Combine(outputDir, filename);
            int distinctColors;
            using (bitmap)
            {
                distinctColors = CountDistinctSampledColors(bitmap);
                bitmap.Save(path);
            }

            captured.Add((path, distinctColors));
        }
        catch (Exception ex)
        {
            skipped.Add((filename, $"{ex.GetType().Name}: {ex.Message}"));
        }
    }

    // 64×64 = 4096 samples: dense enough to land hits on sparse panels (e.g. a single
    // anti-aliased label on a mostly-black background) yet still O(ms) per bitmap.
    private const int SampleGridSize = 64;

    /// <summary>
    /// Samples a <see cref="SampleGridSize"/>×<see cref="SampleGridSize"/> grid of pixels
    /// from the bitmap and returns the count of distinct 32-bit ARGB values. Works for any
    /// 4-byte-per-pixel format (Bgra8888, Rgba8888) — the call only counts diversity, not
    /// color semantics.
    /// </summary>
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

    /// <summary>
    /// Deletes any pre-existing *.png in the output directory. Prevents stale screenshots
    /// from a previous successful run from masking a current silent capture failure.
    /// </summary>
    private static void ClearStalePngs(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.GetFiles(dir, "*.png"))
        {
            try { File.Delete(f); } catch (IOException) { /* file locked — best-effort */ }
        }
    }

    /// <summary>
    /// Resolves the output directory. Checks <c>UI_SHOT_DIR</c> env var first, then walks up
    /// from the test binary to find the repo root (directory containing a <c>.sln</c> file).
    /// </summary>
    private static string ResolveOutputDirectory()
    {
        var envDir = Environment.GetEnvironmentVariable("UI_SHOT_DIR");
        if (!string.IsNullOrEmpty(envDir))
            return envDir;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
                return Path.Combine(dir.FullName, "artifacts", "ui-screenshots");
            dir = dir.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "artifacts", "ui-screenshots");
    }
}
