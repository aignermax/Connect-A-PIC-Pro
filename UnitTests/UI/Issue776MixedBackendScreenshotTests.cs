using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Export;
using CAP.Avalonia.Views.Dialogs;
using CAP_Core.Export;
using Shouldly;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Visual documentation for the mixed-backend GDS export (#776): the gdsfactory export
/// dialog with a design mixing a gdsfactory-native and a nazca-native component, showing
/// (1) the automatic merge notice when the export starts and (2) the notice kept next to
/// the success line after both scripts ran. PNGs land in <c>UI_SHOT_DIR/issue-776/</c>.
/// </summary>
[Trait("Category", "UiScreenshots")]
[Collection("LocalizationSingleton")]
public class Issue776MixedBackendScreenshotTests
{
    private const int CaptureAttempts = 3;

    /// <summary>Captures the warning-only and warning-plus-result dialog states.</summary>
    [AvaloniaFact]
    public void CaptureMixedBackendExportDialogStates()
    {
        // Opt-in like UiScreenshotTests: only runs when screenshots are explicitly requested.
        var shotRoot = Environment.GetEnvironmentVariable("UI_SHOT_DIR");
        if (string.IsNullOrEmpty(shotRoot))
            return;
        var outputDir = Path.Combine(shotRoot, "issue-776");
        Directory.CreateDirectory(outputDir);
        foreach (var stale in Directory.GetFiles(outputDir, "*.png"))
            File.Delete(stale);

        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);
        var scriptPath = Path.Combine(Path.GetTempPath(), $"shot776-{Guid.NewGuid():N}.py");
        var partialPath = CAP.Avalonia.Services.GdsFactoryExport.MixedBackend
            .MixedBackendGdsOrchestrator.PartialScriptPathFor(scriptPath);
        try
        {
            // State 1: export started on a mixed design, save dialog cancelled — the
            // automatic merge notice is already visible in the dialog status.
            var vm = new GdsFactoryExportViewModel(MixedBackendCanvas(), new StubSuccessExportService())
            {
                FileDialogService = new StubFileDialog(null),
            };
            vm.RefreshUnmappedComponents();
            PumpUntilComplete(vm.ExportCommand.ExecuteAsync(null));
            vm.StatusText.ShouldContain("merged into one GDS");
            CaptureDialog(vm, Path.Combine(outputDir, "01-mixed-backend-merge-notice.png"));

            // State 2: full export — nazca partial ran first, then the merging main script;
            // the notice stays visible next to the success line.
            vm.FileDialogService = new StubFileDialog(scriptPath);
            PumpUntilComplete(vm.ExportCommand.ExecuteAsync(null));
            vm.StatusText.ShouldContain("Exported");
            CaptureDialog(vm, Path.Combine(outputDir, "02-mixed-backend-exported.png"));

            ScreenshotArtifacts.WriteText(Path.Combine(outputDir, "manifest.json"), """
            {
              "issue": 776,
              "screenshots": [
                {
                  "file": "01-mixed-backend-merge-notice.png",
                  "description": "gdsfactory export dialog on a design mixing gdsfactory-native and nazca-native components: the automatic mixed-backend merge notice appears in the status area."
                },
                {
                  "file": "02-mixed-backend-exported.png",
                  "description": "After the export: the nazca partial script ran first, the main gdsfactory script merged its GDS; the merge notice stays visible next to the success line."
                }
              ]
            }
            """);
        }
        finally
        {
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
            if (File.Exists(partialPath)) File.Delete(partialPath);
        }

        Directory.GetFiles(outputDir, "*.png").Length.ShouldBe(2);
    }

    /// <summary>
    /// Pumps the headless UI dispatcher until <paramref name="task"/> completes — blocking
    /// with <c>GetResult()</c> would deadlock, because async file I/O in the export posts
    /// its continuations back to this same dispatcher thread.
    /// </summary>
    private static void PumpUntilComplete(Task task)
    {
        while (!task.IsCompleted)
            Dispatcher.UIThread.RunJobs();
        task.GetAwaiter().GetResult();
    }

    private static DesignCanvasViewModel MixedBackendCanvas()
    {
        var canvas = new DesignCanvasViewModel();
        var gf = TestComponentFactory.CreateBasicComponent();
        gf.Identifier = "GF1";
        gf.NazcaFunctionName = "";
        gf.GdsFactoryFunction = "cspdk.sin300.mmi1x2";
        canvas.AddComponent(gf, "SiN MMI");
        var nazca = TestComponentFactory.CreateBasicComponent();
        nazca.Identifier = "NZ1";
        nazca.NazcaFunctionName = "ebeam_y_1550";
        canvas.AddComponent(nazca, "Y-Branch");
        return canvas;
    }

    private static void CaptureDialog(GdsFactoryExportViewModel vm, string path)
    {
        var window = new GdsFactoryExportDialog { DataContext = vm };
        window.Show();
        try
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

            bitmap.ShouldNotBeNull($"CaptureRenderedFrame stayed null after {CaptureAttempts} attempts for {path}");
            using (bitmap)
            {
                ScreenshotArtifacts.SavePng(bitmap, path);
            }
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Export service reporting script success without running Python.</summary>
    private sealed class StubSuccessExportService : GdsExportService
    {
        public override Task<ExportResult> ExportToGdsAsync(string scriptPath, bool generateGds) =>
            Task.FromResult(new ExportResult { ScriptPath = scriptPath, Success = true });
    }

    private sealed class StubFileDialog(string? path) : IFileDialogService
    {
        public Task<string?> ShowSaveFileDialogAsync(string title, string defaultExtension, string filters) =>
            Task.FromResult(path);

        public Task<string?> ShowOpenFileDialogAsync(string title, string filters) =>
            Task.FromResult<string?>(null);
    }
}
