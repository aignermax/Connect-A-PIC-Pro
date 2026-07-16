using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.ViewModels.Components.AddCustomComponent;
using CAP.Avalonia.Views;
using CAP_Core.Export;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Visual walkthrough for issue #701 (Bring-your-own-Component v2): renders the New Component
/// window's changed flow — nazca backend, raw-code authoring, and the selectable S-matrix
/// model — as step-ordered headless PNGs into <c>artifacts/ui-screenshots/issue-701/</c> plus a
/// <c>manifest.json</c> with one-sentence captions. Same Skia harness as
/// <see cref="UiScreenshotTests"/>. Geometry renderers are mocked; no behavior is changed.
/// </summary>
[Trait("Category", "UiScreenshots")]
public class Issue701WalkthroughScreenshotTests
{
    private const int MinDistinctSampledColors = 10;
    private const int SampleGridSize = 64;

    /// <summary>Renders the four walkthrough steps and writes the manifest.</summary>
    [AvaloniaFact]
    public async Task CaptureIssue701Walkthrough()
    {
        var dir = ResolveOutputDirectory();
        Directory.CreateDirectory(dir);
        foreach (var stale in Directory.GetFiles(dir, "*.png"))
            File.Delete(stale);

        var manifest = new List<ManifestEntry>();
        var storeRoot = Path.Combine(Path.GetTempPath(), "lunima-walk-701-" + Guid.NewGuid().ToString("N"));
        try
        {
            var vm = BuildViewModel(storeRoot);
            // Taller than the default 700 so the status line stays in view in raw-code mode
            // (the cell-code editor pushes the scroll content down).
            var window = new NewComponentWindow { DataContext = vm, Width = 520, Height = 800 };
            window.Show();
            PumpRenderLoop();

            Capture(window, dir, "01-initial-gdsfactory.png",
                "The New Component window opens in gdsfactory function-reference mode with the new "
                + "backend selector and the S-matrix model choice defaulting to black box.", manifest);

            vm.SelectedBackend = GeometryBackend.Nazca;
            vm.ComponentName = "my_mmi";
            vm.Module = "mylib";
            vm.Function = "mmi";
            await vm.RunPreviewCommand.ExecuteAsync(null);
            PumpRenderLoop();

            Capture(window, dir, "02-nazca-preview.png",
                "Selecting the new nazca backend and rendering a preview extracts the size and "
                + "pins from which the NazcaOriginOffset is derived on save.", manifest);

            vm.UseRawCode = true;
            vm.RawCode = "with nd.Cell('my_mmi') as c:\n    nd.strt(length=10).put(0, 0)";
            await vm.RunPreviewCommand.ExecuteAsync(null);
            PumpRenderLoop();

            Capture(window, dir, "03-raw-code-authoring.png",
                "Checking the raw-code toggle swaps the function-reference fields for a cell-code "
                + "editor, so a complete pasted nazca cell renders through the raw-code pipeline.",
                manifest);

            vm.SelectedProcess = vm.Processes[0];
            vm.SelectedSMatrixOption = SMatrixSourceOption.For(SMatrixSource.LosslessTwoPort);
            await vm.SaveCommand.ExecuteAsync(null);
            PumpRenderLoop();

            Capture(window, dir, "04-lossless-ideal-saved.png",
                "Choosing the lossless 2-port pass-through ideal and saving stores the component "
                + "in the process's user PDK with the exact unit S-matrix, confirmed in the status "
                + "line.", manifest);

            window.Close();
            Dispatcher.UIThread.RunJobs();
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

        manifest.Count.ShouldBe(4);
    }

    /// <summary>
    /// Builds the ViewModel exactly as the app wires it, with mocked geometry renderers
    /// returning a fixed 2-pin preview (so the lossless 2-port ideal is honestly applicable).
    /// </summary>
    private static NewComponentViewModel BuildViewModel(string storeRoot)
    {
        var preview = new NazcaPreviewResult
        {
            Success = true, XMin = -3, YMin = 3, XMax = 7, YMax = 5,
            Pins = new List<NazcaPreviewPin>
            {
                new() { Name = "o1", X = -3, Y = 4, Angle = 180 },
                new() { Name = "o2", X = 7, Y = 4, Angle = 0 }
            }
        };
        var nazca = new Mock<IComponentPreviewRenderer>();
        var gds = new Mock<IComponentPreviewRenderer>();
        nazca.Setup(n => n.RenderAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(preview);
        nazca.Setup(n => n.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(preview);
        gds.Setup(g => g.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(preview);

        var extractor = new ComponentGeometryExtractor(nazca.Object, gds.Object);
        var store = new UserPdkStore(storeRoot, new PdkJsonSaver(), new PdkLoader());
        return new NewComponentViewModel(extractor, null, store,
            new List<ProcessDefinition> { new() { Name = "Demo Process" } });
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
            return Path.Combine(envDir, "issue-701");

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
                return Path.Combine(dir.FullName, "artifacts", "ui-screenshots", "issue-701");
            dir = dir.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "artifacts", "ui-screenshots", "issue-701");
    }

    /// <summary>One manifest row: PNG file name plus its one-sentence caption.</summary>
    private sealed record ManifestEntry(string File, string Caption);
}
