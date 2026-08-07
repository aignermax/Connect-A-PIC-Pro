using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CAP.Avalonia.Views.Panels;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Properties.Editors;
using CAP_Core.ExternalPorts.LaserSpectrum;
using Shouldly;
using UnitTests.Analysis.AnalysisOutput;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Visual documentation for the realistic laser source model (#819): renders the
/// properties panel with a selected light-source coupler in three states — the
/// default ideal preset, a Gaussian source with the linewidth editor visible, and
/// a Lorentzian source with an adjusted RIN. PNGs + manifest.json land in
/// <c>UI_SHOT_DIR/issue-819/</c>.
/// </summary>
[Trait("Category", "UiScreenshots")]
[Collection("LocalizationSingleton")]
public class Issue819LaserSpectrumScreenshotTests
{
    private const int PanelWidth = 460;
    private const int PanelHeight = 700;
    private const int CaptureAttempts = 3;

    /// <summary>Captures the ideal, Gaussian and Lorentzian editor states.</summary>
    [AvaloniaFact]
    public void CaptureLaserSpectrumEditorStates()
    {
        // Opt-in like UiScreenshotTests: only runs when screenshots are explicitly requested.
        var shotRoot = Environment.GetEnvironmentVariable("UI_SHOT_DIR");
        if (string.IsNullOrEmpty(shotRoot))
            return;
        var outputDir = Path.Combine(shotRoot, "issue-819");
        Directory.CreateDirectory(outputDir);
        foreach (var stale in Directory.GetFiles(outputDir, "*.png"))
            File.Delete(stale);

        var canvas = new DesignCanvasViewModel();
        var coupler = AnalysisOutputTestBed.AddCoupler(canvas, x: 60, y: 60);
        // The editor provider classifies by identifier (SimulationService.IsLightSource).
        coupler.Component.Identifier = "grating_coupler_1";
        coupler.Component.HumanReadableName = "Grating Coupler";

        var vm = MainViewModelTestHelper.CreateMainViewModel(canvas: canvas);
        canvas.SelectedComponent = coupler;
        // MainViewModelTestHelper only registers the generic editor provider, so
        // surface the light-source editor directly (same VM the real provider creates).
        vm.RightPanel.SelectedComponentEditor = new LightSourceEditorViewModel(coupler);
        vm.RightPanel.HasSelectedComponentEditor.ShouldBeTrue(
            "selecting the coupler must surface the light-source editor");

        var panel = new SelectedComponentPropertiesPanel { DataContext = vm };
        var window = new Window
        {
            Width = PanelWidth,
            Height = PanelHeight,
            Content = new ScrollViewer { Content = panel },
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var manifest = new List<object>();
        try
        {
            // State 1: default ideal preset — no linewidth/RIN fields, today's behaviour.
            CaptureWithRetry(window, Path.Combine(outputDir, "01-ideal-default.png"));
            manifest.Add(new
            {
                file = "01-ideal-default.png",
                caption = "Default 'Ideal (single wavelength)' preset: no linewidth field — the "
                    + "simulation behaves identically to pre-#819. RIN stays editable for the "
                    + "eye-diagram noise model.",
            });

            // State 2: Gaussian shape — linewidth (FWHM) editor and 1 nm sampling hint appear.
            coupler.LaserConfig!.LineShape = LaserLineShape.Gaussian;
            Dispatcher.UIThread.RunJobs();
            CaptureWithRetry(window, Path.Combine(outputDir, "02-gaussian-linewidth.png"));
            manifest.Add(new
            {
                file = "02-gaussian-linewidth.png",
                caption = "Gaussian line shape selected: the linewidth (FWHM, nm) editor appears "
                    + "with the hint that the spectrum is sampled at weighted 1 nm steps.",
            });

            // State 3: Lorentzian shape with wider linewidth and adjusted RIN.
            coupler.LaserConfig.LineShape = LaserLineShape.Lorentzian;
            coupler.LaserConfig.LinewidthFwhmNm = 6;
            coupler.LaserConfig.RinDbPerHz = -130;
            Dispatcher.UIThread.RunJobs();
            CaptureWithRetry(window, Path.Combine(outputDir, "03-lorentzian-rin.png"));
            manifest.Add(new
            {
                file = "03-lorentzian-rin.png",
                caption = "Lorentzian line shape with 6 nm FWHM and RIN raised to -130 dB/Hz — "
                    + "the RIN feeds the eye-diagram receiver noise model.",
            });
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }

        ScreenshotArtifacts.WriteText(
            Path.Combine(outputDir, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        Directory.GetFiles(outputDir, "*.png").Length.ShouldBe(3);
    }

    /// <summary>
    /// Captures the window, pumping the dispatcher before every attempt and keeping the
    /// last successful frame (headless rendering can miss frames — same pattern as
    /// <see cref="Issue754AnalysisOutputScreenshotTests"/>).
    /// </summary>
    private static void CaptureWithRetry(Window window, string path)
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
}
