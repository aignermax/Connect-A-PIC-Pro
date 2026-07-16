using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.ViewModels.Components.AddCustomComponent;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.Views;
using CAP_Core.Export;
using CAP_Core.Solvers.Fdtd;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Visual walkthrough for issue #730 (edit-mode rename removes the original component): renders
/// the Edit Component flow — PDK contents before, the prefilled edit window, the rename, the
/// save, and the PDK contents after (no orphaned original) — as step-ordered headless PNGs into
/// <c>artifacts/ui-screenshots/issue-730/</c> plus a <c>manifest.json</c> with one-sentence
/// captions. Same Skia harness as <see cref="UiScreenshotTests"/>; geometry renderers are
/// mocked; no behavior is changed.
/// </summary>
[Trait("Category", "UiScreenshots")]
public class Issue730WalkthroughScreenshotTests
{
    private const int MinDistinctSampledColors = 10;
    private const int MinDistinctColorsSparseFrame = 4;
    private const int SampleGridSize = 64;
    private const string RawCode = "import gdsfactory as gf\ncomponent = gf.components.coupler()";

    /// <summary>Renders the five walkthrough steps and writes the manifest.</summary>
    [AvaloniaFact]
    public async Task CaptureIssue730Walkthrough()
    {
        var dir = ResolveOutputDirectory();
        Directory.CreateDirectory(dir);
        foreach (var stale in Directory.GetFiles(dir, "*.png"))
            File.Delete(stale);

        var manifest = new List<ManifestEntry>();
        var storeRoot = Path.Combine(Path.GetTempPath(), "lunima-walk-730-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (vm, pdkPath) = BuildEditModeViewModel(storeRoot);

            CapturePdkContents(pdkPath, dir, "01-pdk-before-rename.png",
                "The user PDK 'Lib' starts with two components — 'comp1' (about to be edited) "
                + "and 'comp2'.", manifest);

            // Taller than the default so the status line stays in view in raw-code mode
            // (the cell-code editor pushes the scroll content down).
            var window = new NewComponentWindow { DataContext = vm, Width = 520, Height = 800 };
            window.Show();
            PumpRenderLoop();

            Capture(window, dir, "02-edit-mode-opened.png",
                "Edit mode opens the window prefilled with 'comp1', its raw code, and its fixed "
                + "target PDK 'Lib'.", manifest);

            vm.ComponentName = "comp1_v2";
            await vm.RunPreviewCommand.ExecuteAsync(null);
            PumpRenderLoop();

            Capture(window, dir, "03-renamed-to-new-name.png",
                "The user renames the component to 'comp1_v2' — a name that does not exist yet in "
                + "the PDK.", manifest);

            await vm.SaveCommand.ExecuteAsync(null);
            PumpRenderLoop();

            Capture(window, dir, "04-saved.png",
                "Saving stores 'comp1_v2' and, with the same write, removes the entry under the "
                + "old name 'comp1' (the #730 fix).", manifest);

            window.Close();
            Dispatcher.UIThread.RunJobs();

            CapturePdkContents(pdkPath, dir, "05-pdk-after-rename-no-orphan.png",
                "The PDK now holds 'comp1_v2' and the untouched 'comp2' — no orphaned 'comp1' "
                + "duplicate is left behind.", manifest);
        }
        finally
        {
            if (Directory.Exists(storeRoot))
                Directory.Delete(storeRoot, true);
        }

        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        File.WriteAllText(Path.Combine(dir, "manifest.json"), json);

        manifest.Count.ShouldBe(5);
    }

    /// <summary>
    /// Seeds a named user PDK with 'comp1' and 'comp2', builds the ViewModel as the app wires it
    /// (mocked geometry renderers returning a fixed 2-pin preview), and enters edit mode for
    /// 'comp1' via <see cref="NewComponentViewModel.LoadForEdit"/>.
    /// </summary>
    private static (NewComponentViewModel vm, string pdkPath) BuildEditModeViewModel(string storeRoot)
    {
        var store = new UserPdkStore(storeRoot, new PdkJsonSaver(), new PdkLoader());
        var process = new ProcessDefinition { Name = "SiN 300" };
        var pdkPath = store.CreateNamedPdkWithProcess("Lib", process, "gdsfactory", null);
        store.AppendToExistingPdk(pdkPath, SeedComponent("comp1"));
        store.AppendToExistingPdk(pdkPath, SeedComponent("comp2"));

        var preview = new NazcaPreviewResult
        {
            Success = true, XMin = 0, YMin = 0, XMax = 8, YMax = 1.5,
            Pins = new List<NazcaPreviewPin>
            {
                new() { Name = "o1", X = 0, Y = 0.75, Angle = 180 },
                new() { Name = "o2", X = 8, Y = 0.75, Angle = 0 }
            }
        };
        var nazca = new Mock<IComponentPreviewRenderer>();
        var gds = new Mock<IComponentPreviewRenderer>();
        gds.Setup(g => g.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(preview);
        var extractor = new ComponentGeometryExtractor(nazca.Object, gds.Object);
        var fdtd = new Mock<IFdtdSMatrixService>();

        var vm = new NewComponentViewModel(extractor, fdtd.Object, store,
            new List<ProcessDefinition> { process });
        vm.LoadForEdit(new ComponentTemplate
        {
            Name = "comp1",
            RawCode = RawCode,
            RawCodeBackend = "gdsfactory",
            PdkSource = "Lib",
        });
        return (vm, pdkPath);
    }

    private static PdkComponentDraft SeedComponent(string name) => new()
    {
        Name = name, WidthMicrometers = 5, HeightMicrometers = 1,
        RawCode = RawCode, RawCodeBackend = "gdsfactory",
        Pins = new() { new PhysicalPinDraft { Name = "o1" }, new PhysicalPinDraft { Name = "o2" } }
    };

    /// <summary>
    /// Renders the actual on-disk PDK file's component list in a small code-built window — the
    /// left-panel library UI is baked into MainWindow.axaml and cannot be shown headless, so this
    /// replica shows what the library would list. Sparse text frames get a lower color floor.
    /// </summary>
    private static void CapturePdkContents(
        string pdkPath, string dir, string filename, string caption, List<ManifestEntry> manifest)
    {
        var pdk = new PdkLoader().LoadFromFileForEditing(pdkPath);
        var list = new StackPanel { Margin = new Avalonia.Thickness(16), Spacing = 6 };
        list.Children.Add(new TextBlock
        {
            Text = $"User PDK \"Lib\" — {pdk.Components.Count} component(s):",
            FontWeight = Avalonia.Media.FontWeight.Bold,
        });
        foreach (var component in pdk.Components)
            list.Children.Add(new TextBlock { Text = $"  - {component.Name}" });

        var window = new Window { Width = 420, Height = 180, Content = list };
        window.Show();
        PumpRenderLoop();
        Capture(window, dir, filename, caption, manifest, MinDistinctColorsSparseFrame);
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Pumps the headless render timer and dispatcher a few times so bound controls actually
    /// paint the latest ViewModel state before capture.
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
            return Path.Combine(envDir, "issue-730");

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
                return Path.Combine(dir.FullName, "artifacts", "ui-screenshots", "issue-730");
            dir = dir.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "artifacts", "ui-screenshots", "issue-730");
    }

    /// <summary>One manifest row: PNG file name plus its one-sentence caption.</summary>
    private sealed record ManifestEntry(string File, string Caption);
}
