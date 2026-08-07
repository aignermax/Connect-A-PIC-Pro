using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CAP.Avalonia.ViewModels.Analysis.WavelengthSpectrum;
using CAP.Avalonia.Views.Panels;
using CAP_Core.Analysis.WavelengthSpectrum;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Visual walkthrough for issue #816 (wavelength spectrum plot): renders the new Spectrum
/// tab in the real analysis dock in three states — initial parameter form, an MZI sweep
/// result (periodic fringes, legend, design-wavelength marker), and a widened sweep range
/// after the live parameter update. Writes step-ordered PNGs plus a <c>manifest.json</c>
/// into <c>artifacts/ui-screenshots/issue-816/</c>. Opt-in via <c>UI_SHOT_DIR</c>.
/// </summary>
[Trait("Category", "UiScreenshots")]
// Heavy Skia frame captures — CI covers it, local default runs exclude Category=Slow.
[Trait("Category", "Slow")]
[Collection("LocalizationSingleton")]
public class Issue816SpectrumWalkthroughTests
{
    private const int WindowWidth = 980;
    private const int WindowHeight = 420;
    private const int CaptureAttempts = 3;
    private const int MinDistinctSampledColors = 10;
    private const int SampleGridSize = 64;
    private const int SpectrumTabIndex = 2;
    private const double DesignWavelengthNm = 1550;

    /// <summary>Captures the three walkthrough states and writes the caption manifest.</summary>
    [AvaloniaFact]
    public void CaptureIssue816SpectrumWalkthrough()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("UI_SHOT_DIR")))
            return;
        var dir = ResolveOutputDirectory();
        Directory.CreateDirectory(dir);
        foreach (var stale in Directory.GetFiles(dir, "*.png"))
            File.Delete(stale);

        var manifest = new List<ManifestEntry>();
        var vm = MainViewModelTestHelper.CreateMainViewModel();
        vm.BottomPanel.Analysis.IsVisible = true;
        vm.BottomPanel.Analysis.SelectedTabIndex = SpectrumTabIndex;
        vm.BottomPanel.Analysis.DockHeight = 320;

        var window = new Window
        {
            Width = WindowWidth,
            Height = WindowHeight,
            Content = new AnalysisDockPanel { DataContext = vm },
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            Capture(window, dir, "01-spectrum-tab-initial.png",
                "The new Spectrum tab in the analysis dock: wavelength range and step "
                + "count next to Run — no plot until the first sweep.", manifest);

            var spectrum = vm.BottomPanel.Analysis.Spectrum;
            ShowSweepResult(spectrum, startNm: 1500, endNm: 1600,
                "Sweep complete: 101 wavelength points.");
            Dispatcher.UIThread.RunJobs();
            Capture(window, dir, "02-mzi-sweep-result.png",
                "MZI sweep 1500–1600 nm: one transmission curve per output pin with legend, "
                + "periodic pass/dip fringes, and the dashed design-wavelength marker at "
                + "1550 nm.", manifest);

            // Suppress the real (debounced) auto-refresh while staging the widened range —
            // the empty test canvas would otherwise overwrite the staged result.
            spectrum.HasResult = false;
            spectrum.EndNm = 1700;
            ShowSweepResult(spectrum, startNm: 1500, endNm: 1700,
                "Sweep complete: 201 wavelength points.");
            Dispatcher.UIThread.RunJobs();
            Capture(window, dir, "03-live-parameter-update.png",
                "Changing the end wavelength to 1700 nm re-runs the sweep automatically: "
                + "axes rescale to the wider range and more fringes appear — no reload "
                + "needed.", manifest);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }

        ScreenshotArtifacts.WriteText(Path.Combine(dir, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            {
                WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));
        manifest.Count.ShouldBe(3);
    }

    /// <summary>
    /// Puts an ideal-MZI sweep result (bar + cross port) directly on the ViewModel, exactly
    /// as <c>ExecuteSweepAsync</c> would after a real run — the sweep pipeline itself is
    /// covered by <c>MziSpectrumIntegrationTests</c>; here only the rendering matters.
    /// </summary>
    private static void ShowSweepResult(
        WavelengthSpectrumViewModel spectrum, int startNm, int endNm, string status)
    {
        var barPin = Guid.NewGuid();
        var crossPin = Guid.NewGuid();
        var labels = new Dictionary<Guid, string>
        {
            { barPin, "GC out A.o0" }, { crossPin, "GC out B.o0" },
        };

        spectrum.PlotModel = WavelengthSpectrumPlotBuilder.BuildPlotModel(
            new[]
            {
                CreateMziCurve(barPin, startNm, endNm, barPort: true),
                CreateMziCurve(crossPin, startNm, endNm, barPort: false),
            },
            pinId => labels[pinId],
            DesignWavelengthNm);
        spectrum.HasResult = true;
        spectrum.StatusText = status;
    }

    /// <summary>Ideal MZI transmission: cos²/sin² fringes with a 50 nm period.</summary>
    private static TransmissionCurve CreateMziCurve(Guid pinId, int startNm, int endNm, bool barPort)
    {
        const double fringePeriodNm = 50.0;
        int count = endNm - startNm + 1;
        var wavelengths = new double[count];
        var transmission = new double[count];
        for (int i = 0; i < count; i++)
        {
            wavelengths[i] = startNm + i;
            double phase = Math.PI * i / fringePeriodNm;
            double amplitude = barPort ? Math.Cos(phase) : Math.Sin(phase);
            transmission[i] = amplitude * amplitude;
        }
        return new TransmissionCurve(pinId, wavelengths, transmission, isAtNoiseFloor: false);
    }

    /// <summary>Captures the shown window to a PNG, fails on a near-blank frame, records the caption.</summary>
    private static void Capture(
        Window window, string dir, string filename, string caption, List<ManifestEntry> manifest)
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
        bitmap.ShouldNotBeNull($"CaptureRenderedFrame stayed null after {CaptureAttempts} attempts for {filename}");

        int distinctColors;
        using (bitmap)
        {
            distinctColors = CountDistinctSampledColors(bitmap);
            ScreenshotArtifacts.SavePng(bitmap, Path.Combine(dir, filename));
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

    /// <summary>Resolves the walkthrough output directory (repo root's artifacts folder).</summary>
    private static string ResolveOutputDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
                return Path.Combine(dir.FullName, "artifacts", "ui-screenshots", "issue-816");
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "artifacts", "ui-screenshots", "issue-816");
    }

    /// <summary>One manifest row: PNG filename plus its one-sentence caption.</summary>
    private sealed record ManifestEntry(string File, string Caption);
}
