using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CAP.Avalonia.ViewModels.Export.PythonEnvironmentManager;
using CAP.Avalonia.Views.Panels;
using CAP_Core.Export;
using CAP_Core.Export.PythonEnvironmentManager;
using Shouldly;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Visual walkthrough for issue #698 (auto-generated managed-environment names): renders the
/// Python Environment Manager create flow as step-ordered headless PNGs into
/// <c>artifacts/ui-screenshots/issue-698/</c> plus a <c>manifest.json</c> with one-sentence
/// captions. Uses the same Skia headless harness as <see cref="UiScreenshotTests"/>.
/// </summary>
[Trait("Category", "UiScreenshots")]
public class Issue698WalkthroughScreenshotTests
{
    private const int MinDistinctSampledColors = 10;
    private const int SampleGridSize = 64;

    /// <summary>Renders the three walkthrough steps and writes the manifest.</summary>
    [AvaloniaFact]
    public void CaptureIssue698Walkthrough()
    {
        var dir = ResolveOutputDirectory();
        Directory.CreateDirectory(dir);
        foreach (var stale in Directory.GetFiles(dir, "*.png"))
            File.Delete(stale);

        var manifest = new List<ManifestEntry>();
        var registry = new PythonEnvironmentRegistry(Path.Combine(
            Path.GetTempPath(), $"lunima-698-walkthrough-registry-{Guid.NewGuid():N}.json"));

        // A legacy hand-named environment (pre-#698) that stays untouched by the change.
        registry.AddOrUpdate(MakeEnv("nazca", "3.11.9"));
        registry.SetActive("nazca");

        var vm = new PythonEnvironmentManagerViewModel(
            registry,
            new UvBootstrapper(),
            new NazcaPackageInstaller(),
            new EnvironmentHealthChecker(new PythonDiscoveryService()),
            new PythonDiscoveryService(),
            () => null);

        var window = new Window
        {
            // Wide enough that a row's version summary does not clip behind its buttons.
            Width = 640,
            Height = 620,
            Content = new PythonEnvironmentManagerPanel { DataContext = vm }
        };
        window.Show();
        PumpRenderLoop();

        Capture(window, dir, "01-create-form-version-only.png",
            "The create form now asks only for a Python version — the environment name "
            + "textbox is gone because the name is generated automatically.", manifest);

        vm.PythonVersion = "3.12";
        PumpRenderLoop();

        Capture(window, dir, "02-version-picked.png",
            "The user picks 3.12 from the dropdown; the create button needs no further "
            + "input before installing Nazca + gdsfactory.", manifest);

        // Simulate the outcome of two consecutive 3.12 creates: the first gets the
        // auto-generated name py3.12, the second the collision suffix py3.12-2.
        var first = EnvironmentNaming.GenerateName("3.12", registry.Exists);
        registry.AddOrUpdate(MakeEnv(first, "3.12.4"));
        var second = EnvironmentNaming.GenerateName("3.12", registry.Exists);
        registry.AddOrUpdate(MakeEnv(second, "3.12.4"));
        vm.RebuildInterpreters();
        PumpRenderLoop();

        Capture(window, dir, "03-generated-names-in-list.png",
            "After creating, environments appear under their generated names — py3.12, then "
            + "py3.12-2 on collision — while the legacy hand-named 'nazca' entry is untouched.",
            manifest);

        first.ShouldBe("py3.12");
        second.ShouldBe("py3.12-2");

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

    /// <summary>Builds a healthy managed-environment registry entry for rendering.</summary>
    private static PythonEnvironment MakeEnv(string name, string pythonVersion) => new()
    {
        Name = name,
        VenvPath = Path.Combine(UvBootstrapper.EnvironmentsBaseDir, name),
        Status = PythonEnvironmentStatus.Healthy,
        PythonVersion = pythonVersion,
        NazcaVersion = "0.6.1",
        GdsFactoryVersion = "9.5.3",
        HasPyclipper = true,
    };

    /// <summary>
    /// Pumps the headless render timer and dispatcher a few times so bindings and layout
    /// settle before capture.
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
            return Path.Combine(envDir, "issue-698");

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
                return Path.Combine(dir.FullName, "artifacts", "ui-screenshots", "issue-698");
            dir = dir.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "artifacts", "ui-screenshots", "issue-698");
    }

    /// <summary>One manifest row: PNG file name plus its one-sentence caption.</summary>
    private sealed record ManifestEntry(string File, string Caption);
}
