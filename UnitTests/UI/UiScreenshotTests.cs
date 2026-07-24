using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CAP.Avalonia.Services;
using CAP.Avalonia.Views;
using CAP.Avalonia.Views.Panels;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.PdkOffset;
using CAP_DataAccess.Components.ComponentDraftMapper;
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
[Collection("LocalizationSingleton")]
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
        // Opt-in: a full headless Avalonia render is heavy enough to destabilise a desktop
        // session, so this runs only when screenshots are explicitly requested via UI_SHOT_DIR.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("UI_SHOT_DIR")))
            return;
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

        // Transient / eye-diagram charts (round 5 findings 1+2): empty-state renders still catch
        // AXAML/style regressions (e.g. a bad Expander/TrackerControl resource key) even though the
        // in-plot-legend removal and dark tracker only become visible with a populated PlotModel.
        TryCapture(() => new TimeDomainPanel(), vm, 500, 420, outputDir, "TimeDomainPanel.png", captured, skipped);
        TryCapture(() => new EyeDiagramPanel(), vm, 500, 420, outputDir, "EyeDiagramPanel.png", captured, skipped);

        // Component Registry window (#656): the helper wires a stubbed client fed by the
        // committed fixtures (no network). Load the index, set a divergent active process
        // so the "different process" chip renders on the tiles, and select a component so
        // the detail column (parameters + artifact provenance) is visible. It's its own
        // Window, so it is captured directly rather than through TryCapture's host wrapping.
        var registry = vm.Registry;
        registry.EnsureLoaded();
        PumpUntilComplete(registry.IndexLoadTask);
        // Tile previews load async after the grid (#771) — wait so the capture
        // shows the fixture SVGs rendered in the tiles instead of placeholders.
        PumpUntilComplete(registry.PreviewsLoadTask);
        registry.ActiveProcessId = "my-inhouse-fab";
        // y-branch-1x2 is the only component with a committed manifest fixture,
        // so selecting it renders a fully populated detail column.
        registry.SelectedComponent = registry.Components.FirstOrDefault(c => c.Id == "y-branch-1x2");
        PumpUntilComplete(registry.DetailsLoadTask);
        TryCaptureWindow(() => new RegistryBrowserWindow { DataContext = registry }, outputDir,
            "RegistryBrowserWindow.png", captured, skipped);

        // Settings content: the environment manager moved from the Properties sidebar to a
        // Settings page — captured standalone with its own ViewModel (not part of MainViewModel).
        // Seed one active, healthy managed environment so the unified list renders a real row
        // (active marker + status badge + versions + Check / remove buttons) rather than the
        // empty state (issue #645).
        var envRegistry = new CAP_Core.Export.PythonEnvironmentManager.PythonEnvironmentRegistry(
            Path.Combine(Path.GetTempPath(), $"lunima-ui-shot-registry-{Guid.NewGuid():N}.json"));
        envRegistry.AddOrUpdate(new CAP_Core.Export.PythonEnvironmentManager.PythonEnvironment
        {
            Name = "nazca",
            VenvPath = Path.Combine(CAP_Core.Export.PythonEnvironmentManager.UvBootstrapper.EnvironmentsBaseDir, "nazca"),
            Status = CAP_Core.Export.PythonEnvironmentManager.PythonEnvironmentStatus.Healthy,
            PythonVersion = "3.11.9", NazcaVersion = "0.6.1", GdsFactoryVersion = "9.5.3", HasPyclipper = true,
        });
        // A second, inactive environment so the "Set Active" button state renders alongside
        // the active "✓ Active" indicator.
        envRegistry.AddOrUpdate(new CAP_Core.Export.PythonEnvironmentManager.PythonEnvironment
        {
            Name = "nazca-py312",
            VenvPath = Path.Combine(CAP_Core.Export.PythonEnvironmentManager.UvBootstrapper.EnvironmentsBaseDir, "nazca-py312"),
            Status = CAP_Core.Export.PythonEnvironmentManager.PythonEnvironmentStatus.Healthy,
            PythonVersion = "3.12.4", NazcaVersion = "0.6.1", GdsFactoryVersion = null, HasPyclipper = true,
        });
        envRegistry.SetActive("nazca");
        var envVm = new CAP.Avalonia.ViewModels.Export.PythonEnvironmentManager.PythonEnvironmentManagerViewModel(
            envRegistry,
            new CAP_Core.Export.PythonEnvironmentManager.UvBootstrapper(),
            new CAP_Core.Export.PythonEnvironmentManager.NazcaPackageInstaller(),
            new CAP_Core.Export.PythonEnvironmentManager.EnvironmentHealthChecker(
                new CAP_Core.Export.PythonDiscoveryService()),
            new CAP_Core.Export.PythonDiscoveryService(),
            () => Path.Combine(CAP_Core.Export.PythonEnvironmentManager.UvBootstrapper.EnvironmentsBaseDir, "nazca",
                OperatingSystem.IsWindows() ? @"Scripts\python.exe" : "bin/python"));
        TryCapture(() => new PythonEnvironmentManagerPanel(), envVm, 500, 700, outputDir, "PythonEnvironmentManagerPanel.png", captured, skipped);

        // PDK trash flyout: seed a deleted PDK + a removed-components backup so the panel
        // renders real recoverable rows (Restore / permanently-delete) instead of the empty state.
        TryCapture(() => new PdkTrashPanel(), SeedPdkTrashViewModel(), 360, 400, outputDir,
            "PdkTrashPanel.png", captured, skipped);

        // PDK Offset Editor (round 5 findings 3a/3b): a selected component with per-pin deltas
        // and preview source populated renders both fold-outs expanded-capable, so the compact
        // Expander header height/width and the backend-neutral "Origin offset (µm)" label are
        // visible in the same shot. It's its own Window (not a UserControl), so it is captured
        // directly rather than through TryCapture's host-window wrapping.
        TryCaptureWindow(BuildPdkOffsetEditorWindowForScreenshot, outputDir,
            "PdkOffsetEditorWindow.png", captured, skipped);

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

    /// <summary>
    /// Pumps the headless UI dispatcher until <paramref name="task"/> completes,
    /// so async ViewModel loads finish without deadlocking the test thread.
    /// </summary>
    private static void PumpUntilComplete(Task task)
    {
        while (!task.IsCompleted)
            Dispatcher.UIThread.RunJobs();
        task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Builds a <see cref="PdkTrashViewModel"/> backed by a throwaway user-PDK root seeded with
    /// one deleted PDK and one removed-components backup, so the trash panel renders real rows.
    /// </summary>
    private static CAP.Avalonia.ViewModels.Panels.PdkTrash.PdkTrashViewModel SeedPdkTrashViewModel()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lunima-ui-shot-trash-{Guid.NewGuid():N}");
        var store = new CAP_DataAccess.Components.AddCustomComponent.UserPdkStore(
            root, new CAP_DataAccess.Components.ComponentDraftMapper.PdkJsonSaver(),
            new CAP_DataAccess.Components.ComponentDraftMapper.PdkLoader());

        CAP_DataAccess.Components.ComponentDraftMapper.DTOs.PdkComponentDraft Comp(string n) => new()
        {
            Name = n, WidthMicrometers = 5, HeightMicrometers = 1,
            RawCode = "import gdsfactory as gf\ncomponent = gf.components.straight()", RawCodeBackend = "gdsfactory",
            Pins = new() { new() { Name = "o1" }, new() { Name = "o2" } },
        };
        var process = new CAP_DataAccess.Components.ComponentDraftMapper.DTOs.ProcessDefinition { Name = "Demo SOI 220nm" };

        // Deleted whole PDK.
        var libA = store.SaveToNamedPdk("My SiN Library", process, Comp("Ring Resonator"), "gdsfactory", null);
        store.SaveToNamedPdk("My SiN Library", process, Comp("Grating Coupler"), "gdsfactory", null);
        store.MoveToTrash(libA);
        // Removed component (leaves a backup while the PDK lives on).
        var libB = store.SaveToNamedPdk("Prototype Kit", process, Comp("Test MMI"), "gdsfactory", null);
        store.SaveToNamedPdk("Prototype Kit", process, Comp("Spiral Delay"), "gdsfactory", null);
        store.RemoveComponent(libB, "Test MMI");

        var vm = new CAP.Avalonia.ViewModels.Panels.PdkTrash.PdkTrashViewModel(store.CreateTrashService());
        vm.Refresh();
        return vm;
    }

    /// <summary>
    /// Builds a <see cref="PdkOffsetEditorWindow"/> with one component pre-selected and both
    /// fold-outs populated (per-pin deltas + preview source), so the round-5 compact-Expander
    /// and backend-neutral-label fixes are visible in the screenshot without a real GDS render.
    /// </summary>
    private static PdkOffsetEditorWindow BuildPdkOffsetEditorWindowForScreenshot()
    {
        const string pdkJson = """
        {
            "fileFormatVersion": 1,
            "name": "Screenshot Demo",
            "components": [
                {
                    "name": "Demo MMI",
                    "category": "Splitters",
                    "nazcaFunction": "demo.mmi2x2_dp",
                    "widthMicrometers": 40,
                    "heightMicrometers": 20,
                    "nazcaOriginOffsetX": 5.0,
                    "nazcaOriginOffsetY": 10.0,
                    "pins": [
                        { "name": "a0", "offsetXMicrometers": 0,  "offsetYMicrometers": 5 },
                        { "name": "b0", "offsetXMicrometers": 40, "offsetYMicrometers": 5 }
                    ]
                }
            ]
        }
        """;

        var tempFile = Path.Combine(Path.GetTempPath(), $"lunima-ui-shot-pdk-{Guid.NewGuid():N}.json");
        File.WriteAllText(tempFile, pdkJson);

        var vm = new PdkOffsetEditorViewModel(new PdkLoader(), new PdkJsonSaver(), new PdkManagerViewModel())
        {
            FileDialogService = new StubFileDialogService(tempFile),
        };
        vm.LoadPdkFileCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        vm.SelectedComponent = vm.Components[0];
        // ComputePinAlignment normally needs a real GDS render; seed the result directly so the
        // "per-pin deltas" fold-out has rows without spinning up the Python preview pipeline.
        vm.PinAlignmentResults.Add(new PinAlignmentInfo("a0", "a0", 0.02, -0.01, 0.022, true));
        vm.PinAlignmentResults.Add(new PinAlignmentInfo("b0", "b0", -0.15, 0.30, 0.335, false));

        return new PdkOffsetEditorWindow { DataContext = vm };
    }

    /// <summary>Minimal <see cref="IFileDialogService"/> stub that returns a fixed path (or none).</summary>
    private sealed class StubFileDialogService(string? pathToReturn) : IFileDialogService
    {
        public Task<string?> ShowOpenFileDialogAsync(string title, string filters) =>
            Task.FromResult(pathToReturn);

        public Task<string?> ShowSaveFileDialogAsync(string title, string defaultExtension, string filters) =>
            throw new NotSupportedException("Not used by the screenshot harness.");
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
                ScreenshotArtifacts.SavePng(bitmap, path);
            }

            captured.Add((path, distinctColors));
        }
        catch (Exception ex)
        {
            skipped.Add((filename, $"{ex.GetType().Name}: {ex.Message}"));
        }
    }

    /// <summary>
    /// Same capture flow as <see cref="TryCapture"/>, but for a <see cref="Window"/> view
    /// (e.g. <c>PdkOffsetEditorWindow</c>) that must be shown directly rather than nested as
    /// another window's Content — a Window is a TopLevel, not an embeddable Control.
    /// </summary>
    private static void TryCaptureWindow(
        Func<Window> createWindow,
        string outputDir,
        string filename,
        List<(string Path, int DistinctColors)> captured,
        List<(string Name, string Reason)> skipped)
    {
        try
        {
            var window = createWindow();

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
                ScreenshotArtifacts.SavePng(bitmap, path);
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
