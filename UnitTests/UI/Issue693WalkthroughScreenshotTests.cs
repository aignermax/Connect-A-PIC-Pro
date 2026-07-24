using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CAP.Avalonia.ViewModels.Analysis;
using CAP.Avalonia.ViewModels.Analysis.EyeDiagram;
using CAP.Avalonia.ViewModels.Analysis.TimeTrace;
using CAP.Avalonia.Views.Panels;
using CAP_Core.Analysis.EyeDiagram;
using CAP_Core.LightCalculation.TimeDomainSimulation;
using CommunityToolkit.Mvvm.ComponentModel;
using OxyPlot.Axes;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Visual walkthrough for issue #693 (PR: plain wheel scrolls the analysis dock,
/// Ctrl(/Cmd)+wheel zooms the chart): renders the real <see cref="AnalysisDockPanel"/>
/// with a seeded transient waveform, simulates headless wheel input over the plot and
/// writes step-ordered PNGs + <c>manifest.json</c> to <c>artifacts/ui-screenshots/issue-693/</c>.
/// </summary>
[Trait("Category", "UiScreenshots")]
[Collection("LocalizationSingleton")]
public class Issue693WalkthroughScreenshotTests
{
    private const int MinDistinctSampledColors = 10;
    private const int SampleGridSize = 64;

    /// <summary>Renders the five walkthrough steps and writes the manifest.</summary>
    [AvaloniaFact]
    public void CaptureIssue693Walkthrough()
    {
        var dir = ResolveOutputDirectory();
        Directory.CreateDirectory(dir);
        foreach (var stale in Directory.GetFiles(dir, "*.png"))
            File.Delete(stale);

        var manifest = new List<ManifestEntry>();
        CaptureDockSteps(dir, manifest);
        CaptureHelpFlyoutStep(dir, manifest);

        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        File.WriteAllText(Path.Combine(dir, "manifest.json"), json);
        manifest.Count.ShouldBe(5);
    }

    /// <summary>Steps 1–4: the analysis dock — initial state, Ctrl+wheel zoom, plain-wheel scroll, Eye tab.</summary>
    private static void CaptureDockSteps(string dir, List<ManifestEntry> manifest)
    {
        var vm = MainViewModelTestHelper.CreateMainViewModel();
        var analysis = vm.BottomPanel.Analysis;
        SeedTransientResult(analysis.Transient);
        SeedEyeResult(analysis.Eye);
        analysis.IsVisible = true;
        analysis.SetDockHeight(320);

        var dock = new AnalysisDockPanel { DataContext = vm };
        var window = new Window { Width = 920, Height = 400, Content = dock };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var panel = dock.GetVisualDescendants().OfType<TimeDomainPanel>().First();
        var scrollViewer = panel.FindAncestorOfType<ScrollViewer>()!;
        scrollViewer.ScrollToEnd();
        Dispatcher.UIThread.RunJobs();
        scrollViewer.Offset.Y.ShouldBeGreaterThan(0, "panel must overflow the dock so plain-wheel scrolling is demonstrable");

        Capture(window, dir, "01-transient-plot-initial.png",
            "Transient tab of the analysis dock with a waveform result; the plot border's new "
            + "tooltip reads \u201cCtrl+scroll to zoom (Cmd on macOS) \u00b7 right-drag to pan \u00b7 "
            + "scroll to move the panel\u201d.", manifest);

        var xAxis = analysis.Transient.PlotModel.Axes.First(a => a.Position == AxisPosition.Bottom);
        double rangeBefore = xAxis.ActualMaximum - xAxis.ActualMinimum;
        double offsetBefore = scrollViewer.Offset.Y;
        var plot = panel.GetVisualDescendants().OfType<OxyPlot.Avalonia.PlotView>().First();
        var overPlot = plot.TranslatePoint(new Point(plot.Bounds.Width / 2, plot.Bounds.Height / 2), window)!.Value;

        window.MouseWheel(overPlot, new Vector(0, 1), RawInputModifiers.Control);
        Capture(window, dir, "02-ctrl-wheel-zooms-plot.png",
            "Ctrl+wheel (Cmd on macOS) over the plot zooms the time axis in while the dock's "
            + "scroll position stays untouched.", manifest);
        (xAxis.ActualMaximum - xAxis.ActualMinimum).ShouldBeLessThan(rangeBefore, "Ctrl+wheel must zoom the plot");
        scrollViewer.Offset.Y.ShouldBe(offsetBefore, "Ctrl+wheel must not scroll the dock");

        double rangeAfterZoom = xAxis.ActualMaximum - xAxis.ActualMinimum;
        window.MouseWheel(overPlot, new Vector(0, 1));
        Capture(window, dir, "03-plain-wheel-scrolls-dock.png",
            "A plain wheel over the very same plot no longer zooms \u2014 the event bubbles up and "
            + "scrolls the analysis dock (content moved, plot axes unchanged).", manifest);
        scrollViewer.Offset.Y.ShouldBeLessThan(offsetBefore, "plain wheel must scroll the dock");
        (xAxis.ActualMaximum - xAxis.ActualMinimum).ShouldBe(rangeAfterZoom, "plain wheel must not zoom the plot");

        analysis.SelectedTabIndex = 1;
        Dispatcher.UIThread.RunJobs();
        var eyePanel = dock.GetVisualDescendants().OfType<EyeDiagramPanel>().First();
        eyePanel.FindAncestorOfType<ScrollViewer>()!.ScrollToEnd();
        Capture(window, dir, "04-eye-tab-same-behavior.png",
            "The Eye/BER heat map gets the same scroll-friendly controller and tooltip, so both "
            + "analysis charts behave consistently.", manifest);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Step 5: the Transient help flyout with the new zooming/panning paragraph.</summary>
    private static void CaptureHelpFlyoutStep(string dir, List<ManifestEntry> manifest)
    {
        var window = new Window { Width = 460, Height = 760, Content = new TransientHelpFlyout() };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.GetVisualDescendants().OfType<ScrollViewer>().First().ScrollToEnd();
        Capture(window, dir, "05-help-flyout-zoom-note.png",
            "The Transient help flyout (like the Eye one) now documents the behavior: hold Ctrl "
            + "(Cmd on macOS) to zoom, plain scrolling moves the panel, right-drag pans.", manifest);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Seeds the transient VM with a deterministic two-pin waveform (no simulation needed).</summary>
    private static void SeedTransientResult(TimeDomainViewModel transient)
    {
        const int points = 512;
        const double dt = 0.05e-12;
        var time = Enumerable.Range(0, points).Select(i => i * dt).ToArray();
        static double[] Pulse(double[] t, double amplitude, double centerPs, double sigmaPs) =>
            t.Select(x => amplitude * Math.Exp(-Math.Pow((x * 1e12 - centerPs) / sigmaPs, 2))).ToArray();

        var pinA = Guid.NewGuid();
        var pinB = Guid.NewGuid();
        var result = new TimeDomainResult(time, new Dictionary<Guid, double[]>
        {
            [pinA] = Pulse(time, 0.80, 8.0, 1.2),
            [pinB] = Pulse(time, 0.45, 11.0, 1.5),
        });

        var items = TimeTracePlotBuilder.BuildSeriesItems(result, id => id == pinA ? "MMI1.out0" : "MMI1.out1");
        foreach (var item in items)
            transient.Series.Add(item);
        transient.PlotModel = TimeTracePlotBuilder.BuildPlotModel(result, items);
        transient.StatusText = "Done — 2 output pin(s)";
        MarkResultAvailable(transient, "_lastResult", result);
    }

    /// <summary>Seeds the eye VM with a histogram folded from a deterministic PRBS-NRZ trace.</summary>
    private static void SeedEyeResult(EyeDiagramViewModel eye)
    {
        const double bitPeriod = 40e-12;
        const int samplesPerBit = 32;
        const double sampleRate = samplesPerBit / bitPeriod;

        var bits = PrbsGenerator.GenerateBits(PrbsOrder.Prbs7, 160);
        var nrz = PrbsGenerator.ToNrzSamples(bits, samplesPerBit, 1.0);
        // First-order low-pass so the eye shows realistic rise/fall transitions.
        var trace = new double[nrz.Length];
        for (int i = 1; i < nrz.Length; i++)
            trace[i] = trace[i - 1] + 0.25 * (nrz[i] - trace[i - 1]);

        var histogram = EyeDiagramBuilder.Build(trace, sampleRate, bitPeriod);
        eye.PlotModel = EyeDiagramPlotBuilder.BuildPlotModel(histogram);
        eye.StatusText = "Done";
        MarkResultAvailable(eye, "_lastHistogram", histogram);
    }

    /// <summary>Sets the VM's private last-result field and raises <c>HasResult</c> so the plot border shows.</summary>
    private static void MarkResultAvailable(ObservableObject vm, string fieldName, object result)
    {
        vm.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(vm, result);
        typeof(ObservableObject)
            .GetMethod("OnPropertyChanged", BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null, new[] { typeof(string) }, modifiers: null)!
            .Invoke(vm, new object?[] { "HasResult" });
    }

    /// <summary>Captures the shown window to a PNG, fails on a near-blank frame, records the caption.</summary>
    private static void Capture(
        Window window, string dir, string filename, string caption, List<ManifestEntry> manifest)
    {
        PumpRenderLoop();
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

    /// <summary>
    /// Advances the headless render timer so render-loop-driven controls (OxyPlot's PlotView
    /// redraws on animation frames) actually paint before the frame is captured.
    /// </summary>
    private static void PumpRenderLoop()
    {
        for (int i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        Dispatcher.UIThread.RunJobs();
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
            return Path.Combine(envDir, "issue-693");

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
                return Path.Combine(dir.FullName, "artifacts", "ui-screenshots", "issue-693");
            dir = dir.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "artifacts", "ui-screenshots", "issue-693");
    }

    /// <summary>One manifest row: PNG file name plus its one-sentence caption.</summary>
    private sealed record ManifestEntry(string File, string Caption);
}
