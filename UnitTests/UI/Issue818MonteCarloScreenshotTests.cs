using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CAP.Avalonia.ViewModels.Analysis.MonteCarloAnalysis;
using CAP.Avalonia.Views.Panels;
using CAP_Core.Analysis.MonteCarloAnalysis;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Visual documentation for the Monte-Carlo fabrication-variance tab (#818): renders the
/// analysis dock on the Monte Carlo tab and captures (1) the parameter row in
/// spectrum-envelope mode, (2) a populated envelope plot (nominal + p5–p95 band + min/max),
/// and (3) the eye-openness histogram with the nominal marker. The plot data is a
/// deterministic synthetic result built through the real plot builder, so the captures show
/// exactly what a finished run renders. PNGs + manifest.json land in
/// <c>artifacts/ui-screenshots/issue-818/</c>.
/// </summary>
[Trait("Category", "UiScreenshots")]
[Collection("LocalizationSingleton")]
public class Issue818MonteCarloScreenshotTests
{
    private const int WindowWidth = 1000;
    private const int WindowHeight = 620;
    private const int CaptureAttempts = 3;
    private const int RunCount = 200;
    private const int StartNm = 1500;
    private const int EndNm = 1600;
    private const int StepCount = 21;

    /// <summary>Captures the empty, envelope-result, and histogram-result states.</summary>
    [AvaloniaFact]
    public void CaptureMonteCarloTabStates()
    {
        // Opt-in like UiScreenshotTests: only runs when screenshots are explicitly requested.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("UI_SHOT_DIR")))
            return;
        var outputDir = ResolveOutputDirectory();
        Directory.CreateDirectory(outputDir);
        foreach (var stale in Directory.GetFiles(outputDir, "*.png"))
            File.Delete(stale);

        var vm = MainViewModelTestHelper.CreateMainViewModel();
        vm.BottomPanel.Analysis.IsVisible = true;
        vm.BottomPanel.Analysis.SelectedTabIndex = 2;
        vm.BottomPanel.Analysis.SetDockHeight(420);
        var mc = vm.BottomPanel.Analysis.MonteCarlo;

        var dock = new AnalysisDockPanel { DataContext = vm };
        var window = new Window { Width = WindowWidth, Height = WindowHeight, Content = dock };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            // State 1: spectrum-envelope mode before any run — parameter row including
            // the wavelength range that only this metric shows.
            CaptureWithRetry(window, Path.Combine(outputDir, "01-monte-carlo-tab.png"));

            // State 2: a finished spectrum run — envelope plot + numeric spread summary.
            var spectrumResult = BuildSyntheticSpectrumResult(out var wavelengths);
            mc.PlotModel = MonteCarloPlotBuilder.BuildEnvelopePlot(wavelengths, spectrumResult, "GC out.o1");
            mc.SummaryText = string.Join(Environment.NewLine,
                "Jittered parameters:  6",
                "Nominal worst IL:     -5.02 dB",
                "Monte-Carlo worst IL: -6.31 dB");
            mc.StatusText = $"Monte Carlo complete: {RunCount} runs";
            Dispatcher.UIThread.RunJobs();
            CaptureWithRetry(window, Path.Combine(outputDir, "02-spectrum-envelope.png"));

            // State 3: eye-openness metric — histogram with the nominal marker; the
            // wavelength inputs disappear because they only apply to the spectrum metric.
            mc.SelectedMetric = mc.Metrics[1];
            var (histogram, nominalEye) = BuildSyntheticEyeDistribution();
            mc.PlotModel = MonteCarloPlotBuilder.BuildHistogramPlot(histogram, nominalEye);
            mc.SummaryText = string.Join(Environment.NewLine,
                "Jittered parameters: 6",
                "Nominal eye height:  8.120E-004",
                "p5 / p50 / p95:      6.905E-004 / 8.101E-004 / 9.288E-004",
                "Open-eye yield:      100.0 %");
            Dispatcher.UIThread.RunJobs();
            CaptureWithRetry(window, Path.Combine(outputDir, "03-eye-openness-histogram.png"));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }

        WriteManifest(outputDir);
        Directory.GetFiles(outputDir, "*.png").Length.ShouldBe(3);
    }

    /// <summary>
    /// Deterministic stand-in for a spectrum Monte-Carlo run: a Gaussian dip around
    /// 1550 nm as the nominal IL curve plus seeded Gaussian scatter per run.
    /// </summary>
    private static MonteCarloResult BuildSyntheticSpectrumResult(out int[] wavelengths)
    {
        int[] range = Enumerable.Range(0, StepCount)
            .Select(i => StartNm + i * (EndNm - StartNm) / (StepCount - 1))
            .ToArray();
        double[] nominal = range
            .Select(wl => -3.0 - 2.0 * Math.Exp(-Math.Pow((wl - 1550.0) / 25.0, 2)))
            .ToArray();

        var sampler = new GaussianSampler(seed: 818);
        var runs = new List<IReadOnlyList<double>>();
        for (int run = 0; run < RunCount; run++)
        {
            double shift = sampler.NextGaussian() * 6.0;
            double offset = sampler.NextGaussian() * 0.25;
            runs.Add(range
                .Select(wl => -3.0 + offset - 2.0 * Math.Exp(-Math.Pow((wl - 1550.0 - shift) / 25.0, 2)))
                .ToArray());
        }

        wavelengths = range;
        return new MonteCarloResult(nominal, runs);
    }

    /// <summary>Deterministic eye-openness distribution around a nominal eye height.</summary>
    private static (DistributionHistogram Histogram, double NominalEye) BuildSyntheticEyeDistribution()
    {
        const double nominalEye = 8.12e-4;
        var sampler = new GaussianSampler(seed: 818);
        double[] samples = Enumerable.Range(0, RunCount)
            .Select(_ => nominalEye * (1.0 + 0.09 * sampler.NextGaussian()))
            .ToArray();
        return (DistributionHistogram.Create(samples), nominalEye);
    }

    private static void WriteManifest(string outputDir)
    {
        const string manifest = """
        [
          {"file": "01-monte-carlo-tab.png", "caption": "New Monte Carlo tab in the analysis dock: metric, runs, sigma, seed and the wavelength range shown for the spectrum-envelope metric."},
          {"file": "02-spectrum-envelope.png", "caption": "Finished spectrum run: nominal curve with the p5-p95 fabrication band and dashed min/max extremes (no 1000-curve overplot) plus the numeric spread summary."},
          {"file": "03-eye-openness-histogram.png", "caption": "Eye-openness metric: distribution histogram with the nominal eye height marked; the wavelength inputs disappear because they only apply to the spectrum metric."}
        ]
        """;
        ScreenshotArtifacts.WriteText(Path.Combine(outputDir, "manifest.json"), manifest);
    }

    /// <summary>Repo-root <c>artifacts/ui-screenshots/issue-818</c> (or <c>UI_SHOT_DIR/issue-818</c>).</summary>
    private static string ResolveOutputDirectory()
    {
        var envDir = Environment.GetEnvironmentVariable("UI_SHOT_DIR");
        if (!string.IsNullOrEmpty(envDir))
            return Path.Combine(envDir, "issue-818");

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
                return Path.Combine(dir.FullName, "artifacts", "ui-screenshots", "issue-818");
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "artifacts", "ui-screenshots", "issue-818");
    }

    /// <summary>
    /// Captures the window, pumping the dispatcher before every attempt and keeping the
    /// last successful frame (headless rendering can miss frames).
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
