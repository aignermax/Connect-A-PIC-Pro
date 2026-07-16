using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.ComponentSettings;
using CAP.Avalonia.ViewModels.Export;
using CAP.Avalonia.Views;
using CAP.Avalonia.Views.Dialogs;
using CAP_Core.Export;
using CAP_DataAccess.Persistence.PIR;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Visual walkthrough for issue #646 (mixed Nazca + gdsfactory export): renders the UI flow
/// as step-ordered headless PNGs into <c>artifacts/ui-screenshots/issue-646/</c> plus a
/// <c>manifest.json</c> with one-sentence captions. Preview services are mocked so no
/// Python/Nazca/gdsfactory is required — the test only exercises the Avalonia views.
/// </summary>
[Trait("Category", "UiScreenshots")]
public class Issue646WalkthroughScreenshotTests
{
    private const int MinDistinctSampledColors = 10;
    private const int SampleGridSize = 64;

    private const string TemplateCode = "def component():\n    return pdk.strt()\n";
    private const string NazcaOverrideCode =
        "import nazca as nd\n"
        + "with nd.Cell(name='ovr_box') as _c:\n"
        + "    nd.Polygon(points=[(0,0),(10,0),(10,5),(0,5)], layer=1).put(0, 0)\n"
        + "def component():\n"
        + "    return _c\n";

    /// <summary>Renders the three walkthrough steps and writes the manifest.</summary>
    [AvaloniaFact]
    public async Task CaptureIssue646Walkthrough()
    {
        var dir = ResolveOutputDirectory();
        Directory.CreateDirectory(dir);
        foreach (var stale in Directory.GetFiles(dir, "*.png"))
            File.Delete(stale);

        var manifest = new List<ManifestEntry>();
        var overrides = new Dictionary<string, NazcaCodeOverride>
        {
            ["NazcaOvr"] = new NazcaCodeOverride { RawCode = NazcaOverrideCode, Backend = OverrideBackend.Nazca }
        };

        await CaptureComponentSettingsSteps(dir, overrides, manifest);
        CaptureExportDialogStep(dir, overrides, manifest);

        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        File.WriteAllText(Path.Combine(dir, "manifest.json"), json);

        manifest.Count.ShouldBe(3);
    }

    /// <summary>Steps 1–2: the per-instance override editor with the Nazca | gdsfactory toggle.</summary>
    private static async Task CaptureComponentSettingsSteps(
        string dir, Dictionary<string, NazcaCodeOverride> overrides, List<ManifestEntry> manifest)
    {
        var vm = new ComponentSettingsDialogViewModel(new Mock<IFileDialogService>().Object);
        vm.Configure(
            entityKey: "NazcaOvr",
            smatrixKey: "NazcaOvr",
            displayName: "ebeam_y_1550 (instance)",
            storedSMatrices: new Dictionary<string, ComponentSMatrixData>(),
            liveComponent: TestComponentFactory.CreateBasicComponent(),
            storedNazcaOverrides: overrides,
            templateFunctionName: "ebeam_y_1550",
            templateModuleName: "ubcpdk",
            nazcaPreviewService: MockPreviewService(),
            nazcaTemplateCode: TemplateCode,
            gdsFactoryPreviewService: MockPreviewService());

        // 700×640 keeps the override-editor section (the part this PR changes) dominant;
        // the empty S-matrix section above it would otherwise pad the frame with dead space.
        var dialog = new ComponentSettingsDialog { DataContext = vm, Width = 700, Height = 640 };
        dialog.Show();
        Dispatcher.UIThread.RunJobs();

        // Validate the override through the (mocked) preview so the editor shows the
        // post-"Run preview" state a user sees before applying.
        await vm.NazcaCodeEditor!.RunPreviewCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Capture(dialog, dir, "01-override-editor-nazca-backend.png",
            "Component Settings: the per-instance override editor with the Backend toggle on "
            + "Nazca — this instance's custom geometry is authored as Nazca code.", manifest);

        vm.NazcaCodeEditor.UseGdsFactoryBackend = true;
        Dispatcher.UIThread.RunJobs();

        Capture(dialog, dir, "02-override-editor-gdsfactory-backend.png",
            "Switching the toggle to gdsfactory re-targets the docs button, quick-help and "
            + "preview to the gdsfactory backend.", manifest);

        dialog.Close();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Step 3: the export dialog announcing the Nazca-rendered-and-merged instances.</summary>
    private static void CaptureExportDialogStep(
        string dir, Dictionary<string, NazcaCodeOverride> overrides, List<ManifestEntry> manifest)
    {
        var canvas = new DesignCanvasViewModel();
        var overridden = TestComponentFactory.CreateBasicComponent();
        overridden.Identifier = "NazcaOvr";
        overridden.NazcaFunctionName = "ebeam_y_1550";
        canvas.AddComponent(overridden, "NazcaOvr");

        var plain = TestComponentFactory.CreateBasicComponent();
        plain.Identifier = "Y1";
        plain.NazcaFunctionName = "ebeam_y_1550";
        plain.PhysicalX = 300;
        canvas.AddComponent(plain, "Y1");

        var vm = new GdsFactoryExportViewModel(canvas, new GdsExportService(), new Mock<IUrlLauncher>().Object)
        {
            OverridesProvider = () => overrides
        };
        vm.RefreshUnmappedComponents();
        vm.BackendMismatches.ShouldContain("NazcaOvr");

        var dialog = new GdsFactoryExportDialog { DataContext = vm };
        dialog.Show();
        Dispatcher.UIThread.RunJobs();

        Capture(dialog, dir, "03-export-dialog-mixed-backend-info.png",
            "The gdsfactory export dialog lists Nazca-backend overrides that are rendered by "
            + "Nazca and merged into the single exported GDS.", manifest);

        dialog.Close();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Builds a deterministic mocked preview service (no Python required).</summary>
    private static NazcaComponentPreviewService MockPreviewService()
    {
        var ok = new NazcaPreviewResult
        {
            Success = true,
            XMin = 0, YMin = 0, XMax = 12, YMax = 6,
            Source = "def ebeam_y_1550():\n    # original PDK source (reference only)\n    ...\n",
            Pins = new List<NazcaPreviewPin>()
        };
        var mock = new Mock<NazcaComponentPreviewService>(MockBehavior.Loose,
            "python3", "preview.py", (TimeSpan?)TimeSpan.FromSeconds(5), (ProcessLaunchFactory?)null)
        { CallBase = false };
        mock.Setup(s => s.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ok);
        mock.Setup(s => s.RenderAsync(
                It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ok);
        return mock.Object;
    }

    /// <summary>Captures the shown window to a PNG, fails on a near-blank frame, records the caption.</summary>
    private static void Capture(
        Window window, string dir, string filename, string caption, List<ManifestEntry> manifest)
    {
        Dispatcher.UIThread.RunJobs();
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
            return Path.Combine(envDir, "issue-646");

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
                return Path.Combine(dir.FullName, "artifacts", "ui-screenshots", "issue-646");
            dir = dir.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "artifacts", "ui-screenshots", "issue-646");
    }

    /// <summary>One manifest row: PNG file name plus its one-sentence caption.</summary>
    private sealed record ManifestEntry(string File, string Caption);
}
