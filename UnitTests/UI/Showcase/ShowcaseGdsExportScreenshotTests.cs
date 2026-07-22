using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.PdkOffset;
using CAP.Avalonia.Views;
using CAP_DataAccess.Components.ComponentDraftMapper;
using Shouldly;
using UnitTests.UI.Flows;
using Xunit;

namespace UnitTests.UI.Showcase;

/// <summary>
/// v0.12 feature-showcase: the manufacturing story ("from canvas to fab"). Composes three
/// panes into one PNG: (1) the staged hero chip with the unified Export flyout open — GDS
/// via gdsfactory and via Nazca one click from the canvas, (2) the PDK offset/calibration
/// editor on the bundled SiEPIC EBeam PDK with the rendered grating-coupler GDS geometry
/// and its pin aligned at Δ 0.00 µm, and (3) the Check-All calibration report, all rows
/// green. Opt-in via <c>UI_SHOT_DIR</c>; the PNG lands in <c>UI_SHOT_DIR/v0.12/</c>.
/// </summary>
[Trait("Category", "Showcase")]
[Collection("LocalizationSingleton")]
public class ShowcaseGdsExportScreenshotTests
{
    private const double OverlayZoom = 3.5;

    /// <summary>SiEPIC components staged as rows of the Check-All calibration report.</summary>
    private static readonly string[] ReportComponents =
    {
        "Y-Branch 1550", "Directional Coupler TE 1550", "Broadband DC TE 1550",
        "DC Halfring-Straight", "Contra-Directional Coupler", "Grating Coupler TE 1550",
        "Taper TE 1550", "Adiabatic Coupler TE 1550",
    };

    [AvaloniaFact]
    public async Task CaptureGdsExport()
    {
        if (!ShowcaseCapture.Enabled) return;
        await ShowcaseCapture.WithEnglishUiAsync(async () =>
        {
            var (mainVm, mainWindow, _) = await ShowcaseCircuit.BootStagedMainWindowAsync();
            StageExportMenuPane(mainWindow, mainVm);
            using var mainFrame = ShowcaseCapture.CaptureFrame(mainWindow, "gds-export (export flyout)");
            mainWindow.Close();
            Dispatcher.UIThread.RunJobs();

            var editorWindow = BuildCalibrationPane();
            using var editorFrame = ShowcaseCapture.CaptureFrame(editorWindow, "gds-export (siepic calibration)");
            var (editorPane, reportPane) = EditorCrops(editorWindow);
            editorWindow.Close();
            Dispatcher.UIThread.RunJobs();

            ShowcaseCapture.ComposeSideBySide(
                Path.Combine(ShowcaseCapture.OutputDirectory(), "gds-export.png"),
                new[]
                {
                    ((Bitmap)mainFrame, new PixelRect(0, 0, mainFrame.PixelSize.Width, mainFrame.PixelSize.Height)),
                    (editorFrame, editorPane),
                    (editorFrame, reportPane),
                });
        });
    }

    /// <summary>Narrows the main window onto toolbar + chip (properties column hidden) and
    /// opens the toolbar's unified Export flyout (all formats, incl. both GDS back-ends).</summary>
    private static void StageExportMenuPane(MainWindow window, CAP.Avalonia.ViewModels.MainViewModel vm)
    {
        window.Width = 1200;
        window.Height = 900;
        UiInput.Descendants<Border>(window).First(b => b.Name == "RightPanelBorder").IsVisible = false;
        Dispatcher.UIThread.RunJobs();
        ShowcaseCircuit.FitView(window, vm);

        var exportButton = UiInput.Descendants<Button>(window).First(b =>
            (b.Content as TextBlock)?.Text ==
            LocalizationService.Instance.Translate("Toolbar.ExportButton"));
        exportButton.Flyout.ShouldNotBeNull("the Export button must carry the format flyout");
        exportButton.Flyout!.ShowAt(exportButton);
        UiInput.RunJobs();
        ShowcaseCapture.PumpRenderLoop();
    }

    /// <summary>
    /// The offset/calibration editor on the REAL bundled SiEPIC EBeam JSON: grating coupler
    /// selected with rendered GDS geometry and 1/1 pins aligned, plus a staged Check-All report.
    /// </summary>
    private static PdkOffsetEditorWindow BuildCalibrationPane()
    {
        var pdkPath = FindBundledSiepicJson();
        var drafts = new PdkLoader().LoadFromFileForEditing(pdkPath).Components;
        var manager = new PdkManagerViewModel();
        manager.RegisterPdk("SiEPIC EBeam PDK", pdkPath, isBundled: true, componentCount: drafts.Count);

        var vm = new PdkOffsetEditorViewModel(
            new PdkLoader(), new PdkJsonSaver(), manager,
            ShowcaseSiepicGeometry.StubPreviewService(drafts));
        var window = new PdkOffsetEditorWindow { Width = 1010, Height = 1400, DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        vm.SelectedInstalledPdk = manager.LoadedPdks.Single();
        Dispatcher.UIThread.RunJobs();
        vm.SelectedComponent = vm.Components.First(c => c.ComponentName == "Grating Coupler TE 1550");
        for (int i = 0; i < 10 && !vm.HasNazcaOverlay; i++)
            Dispatcher.UIThread.RunJobs();

        vm.HasNazcaOverlay.ShouldBeTrue("the stubbed grating-coupler render must populate the overlay");
        vm.PinAlignmentResults.Count.ShouldBe(1);
        vm.PinAlignmentResults.All(p => p.IsAligned)
            .ShouldBeTrue("the bundled calibration must align the grating-coupler pin");

        SeedBatchReport(vm);
        StageEditorChrome(vm, window);
        return window;
    }

    /// <summary>
    /// Stages the Check-All report rows through the production evaluator: each bundled draft
    /// against its calibration-perfect render — every row ✓ Aligned at Δ 0.00 µm.
    /// </summary>
    private static void SeedBatchReport(PdkOffsetEditorViewModel vm)
    {
        foreach (var name in ReportComponents)
        {
            var draft = vm.Components.First(c => c.ComponentName == name).Draft;
            vm.BatchCheckResults.Add(PdkOffsetCalibration.Evaluate(
                draft, ShowcaseSiepicGeometry.PerfectResultFor(draft),
                PdkOffsetEditorViewModel.PinAlignmentToleranceMicrometers,
                PdkOffsetEditorViewModel.PinAlignmentCheckToleranceMicrometers));
        }
        vm.BatchCheckResults.All(r => r.Status == ComponentCheckStatus.Aligned)
            .ShouldBeTrue("all staged report rows must evaluate as Aligned");
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Arranges the editor for the composite: per-pin deltas expanded, the raw pin-position
    /// table hidden (the verdicts carry the story), the overlay zoomed onto the grating
    /// geometry, and the origin label nudged below the crosshair (the calibrated pin sits
    /// exactly on the origin, which would otherwise overlap the two labels).
    /// </summary>
    private static void StageEditorChrome(PdkOffsetEditorViewModel vm, PdkOffsetEditorWindow window)
    {
        UiInput.Descendants<Expander>(window)
            .First(e => Equals(e.Header, LocalizationService.Instance.Translate("PdkOffset.PerPinDeltas")))
            .IsExpanded = true;

        UiInput.Descendants<TextBlock>(window)
            .First(t => t.Text == LocalizationService.Instance.Translate("PdkOffset.PinPositionsTitle"))
            .FindAncestorOfType<Border>()!.IsVisible = false;

        var slider = window.FindControl<Slider>("ZoomSlider");
        slider.ShouldNotBeNull();
        slider!.Value = OverlayZoom;
        var scroller = window.FindControl<ScrollViewer>("OverlayScrollViewer");
        scroller.ShouldNotBeNull();
        scroller!.Height = 340;
        UiInput.RunJobs();
        ShowcaseCapture.PumpRenderLoop();

        double centerX = (vm.CanvasComponentLeft + vm.CanvasComponentWidth / 2) * OverlayZoom;
        double centerY = (vm.CanvasComponentTop + vm.CanvasComponentHeight / 2) * OverlayZoom;
        scroller.Offset = new Vector(
            Math.Max(0, centerX - scroller.Viewport.Width / 2),
            Math.Max(0, centerY - scroller.Viewport.Height / 2));
        UiInput.RunJobs();

        // LAST (any overlay redraw would re-add them at the default spot): the calibrated
        // port sits exactly on the origin at the canvas' right edge, so the default
        // right-of-marker labels would clip and collide — stack both to the left instead.
        var overlayCanvas = window.FindControl<global::Avalonia.Controls.Canvas>("OverlayCanvas");
        overlayCanvas.ShouldNotBeNull();
        var pinMarker = vm.PinMarkers.Single();
        var pinLabel = overlayCanvas!.Children.OfType<TextBlock>().First(t => t.Text == pinMarker.Name);
        global::Avalonia.Controls.Canvas.SetLeft(pinLabel, pinMarker.CanvasX - 44);
        global::Avalonia.Controls.Canvas.SetTop(pinLabel, pinMarker.CanvasY - 18);
        var originLabel = overlayCanvas.Children.OfType<TextBlock>().First(t => t.Text == "origin");
        global::Avalonia.Controls.Canvas.SetLeft(originLabel, vm.CanvasOriginX - 44);
        global::Avalonia.Controls.Canvas.SetTop(originLabel, vm.CanvasOriginY + 10);
        UiInput.RunJobs();
    }

    /// <summary>Window-space crops of the editor column (header/offset/alignment/overlay)
    /// and the Check-All report incl. the save/status bar — two separate panes.</summary>
    private static (PixelRect EditorPane, PixelRect ReportPane) EditorCrops(PdkOffsetEditorWindow window)
    {
        var overlayScroller = window.FindControl<ScrollViewer>("OverlayScrollViewer")!;
        var rightColumn = ShowcaseCapture.BoundsIn(
            window, overlayScroller.FindAncestorOfType<ScrollViewer>()!);
        var reportBorder = ShowcaseCapture.BoundsIn(window, UiInput.Descendants<TextBlock>(window)
            .First(t => t.Text == LocalizationService.Instance.Translate("PdkOffset.BatchReport"))
            .FindAncestorOfType<Border>()!);
        var saveBar = ShowcaseCapture.BoundsIn(window, UiInput.Descendants<Button>(window)
            .First(b => Equals(b.Content, LocalizationService.Instance.Translate("PdkOffset.SavePdk")))
            .FindAncestorOfType<Border>()!);

        var editorPane = new PixelRect(
            rightColumn.X, 0, rightColumn.Width + 8, reportBorder.Y - 4);
        var reportPane = new PixelRect(
            rightColumn.X, reportBorder.Y - 4, rightColumn.Width + 8,
            saveBar.Bottom - reportBorder.Y + 12);
        return (editorPane, reportPane);
    }

    /// <summary>Walks up from the test output directory to the repo's bundled SiEPIC JSON.</summary>
    private static string FindBundledSiepicJson()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "CAP-DataAccess", "PDKs", "siepic-ebeam-pdk.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            $"bundled siepic-ebeam-pdk.json not found above {AppContext.BaseDirectory}");
    }
}
