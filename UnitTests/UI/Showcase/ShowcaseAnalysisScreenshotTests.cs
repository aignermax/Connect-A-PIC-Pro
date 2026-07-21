using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Analysis;
using CAP.Avalonia.ViewModels.Analysis.EyeDiagram;
using CAP.Avalonia.ViewModels.Analysis.TimeTrace;
using CAP.Avalonia.Views.Panels;
using CAP_Core.Analysis.EyeDiagram;
using CAP_Core.LightCalculation.TimeDomainSimulation;
using CommunityToolkit.Mvvm.ComponentModel;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.UI.Showcase;

/// <summary>
/// v0.12 feature-showcase: the analysis dock with a seeded multi-output transient result
/// (Transient tab) and a PRBS-driven eye/BER heat map (Eye tab). Deterministic synthetic
/// data drives the real plot builders — no simulation run needed.
/// Opt-in via <c>UI_SHOT_DIR</c>; PNGs land in <c>UI_SHOT_DIR/v0.12/</c>.
/// </summary>
[Trait("Category", "Showcase")]
[Collection("LocalizationSingleton")]
public class ShowcaseAnalysisScreenshotTests
{
    [AvaloniaFact]
    public async Task CaptureTransientAndEyeAnalysis()
    {
        if (!ShowcaseCapture.Enabled) return;
        await ShowcaseCapture.WithEnglishUiAsync(() =>
        {
            var vm = MainViewModelTestHelper.CreateMainViewModel();
            var analysis = vm.BottomPanel.Analysis;
            SeedTransientResult(analysis.Transient);
            SeedEyeResult(analysis.Eye);
            analysis.IsVisible = true;
            analysis.SetDockHeight(430);

            var dock = new AnalysisDockPanel { DataContext = vm };
            var window = new Window { Width = 1340, Height = 480, Content = dock };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            ScrollTabToPlot<TimeDomainPanel>(dock);
            ShowcaseCapture.CaptureWindow(window,
                Path.Combine(ShowcaseCapture.OutputDirectory(), "transient-analysis.png"));

            analysis.SelectedTabIndex = 1;
            Dispatcher.UIThread.RunJobs();
            ScrollTabToPlot<EyeDiagramPanel>(dock);
            ShowcaseCapture.CaptureWindow(window,
                Path.Combine(ShowcaseCapture.OutputDirectory(), "eye-analysis.png"));

            window.Close();
            Dispatcher.UIThread.RunJobs();
            return Task.CompletedTask;
        });
    }

    /// <summary>Scrolls the active tab's panel so its plot (below the controls) is in view.</summary>
    private static void ScrollTabToPlot<TPanel>(AnalysisDockPanel dock) where TPanel : Control
    {
        var panel = dock.GetVisualDescendants().OfType<TPanel>().First();
        panel.FindAncestorOfType<ScrollViewer>()?.ScrollToEnd();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Seeds a three-output transient result: MZI bar/cross pulses + detector tail.</summary>
    private static void SeedTransientResult(TimeDomainViewModel transient)
    {
        const int points = 640;
        const double dt = 0.05e-12;
        var time = Enumerable.Range(0, points).Select(i => i * dt).ToArray();
        static double[] Pulse(double[] t, double amplitude, double centerPs, double sigmaPs) =>
            t.Select(x => amplitude * Math.Exp(-Math.Pow((x * 1e12 - centerPs) / sigmaPs, 2))).ToArray();

        var (bar, cross, detector) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var result = new TimeDomainResult(time, new Dictionary<Guid, double[]>
        {
            [bar] = Pulse(time, 0.82, 9.0, 1.3),
            [cross] = Pulse(time, 0.44, 12.0, 1.6),
            [detector] = Pulse(time, 0.27, 16.0, 2.4),
        });

        var names = new Dictionary<Guid, string>
        {
            [bar] = "MZI_combiner.out1 (bar)",
            [cross] = "MZI_combiner.out2 (cross)",
            [detector] = "PD1.in",
        };
        var items = TimeTracePlotBuilder.BuildSeriesItems(result, id => names[id]);
        foreach (var item in items)
            transient.Series.Add(item);
        transient.PlotModel = TimeTracePlotBuilder.BuildPlotModel(result, items);
        transient.StatusText = "Done — 3 output pin(s)";
        MarkResultAvailable(transient, "_lastResult", result);
    }

    /// <summary>Seeds the eye VM with a histogram folded from a low-passed PRBS-7 NRZ trace.</summary>
    private static void SeedEyeResult(EyeDiagramViewModel eye)
    {
        const double bitPeriod = 40e-12;
        const int samplesPerBit = 32;
        const double sampleRate = samplesPerBit / bitPeriod;

        var bits = PrbsGenerator.GenerateBits(PrbsOrder.Prbs7, 320);
        var nrz = PrbsGenerator.ToNrzSamples(bits, samplesPerBit, 1.0);
        // First-order low-pass (rise ≈ a third of the bit) + deterministic noise →
        // a classic NRZ eye with visible crossing fans instead of square edges.
        var noise = new Random(42);
        var trace = new double[nrz.Length];
        for (int i = 1; i < nrz.Length; i++)
            trace[i] = trace[i - 1] + 0.12 * (nrz[i] - trace[i - 1]) + (noise.NextDouble() - 0.5) * 0.03;

        var histogram = EyeDiagramBuilder.Build(trace, sampleRate, bitPeriod);
        eye.PlotModel = EyeDiagramPlotBuilder.BuildPlotModel(histogram);
        eye.StatusText = "Done";
        MarkResultAvailable(eye, "_lastHistogram", histogram);
    }

    /// <summary>Sets the VM's private last-result field and raises <c>HasResult</c>.</summary>
    private static void MarkResultAvailable(ObservableObject vm, string fieldName, object result)
    {
        vm.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(vm, result);
        typeof(ObservableObject)
            .GetMethod("OnPropertyChanged", BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null, new[] { typeof(string) }, modifiers: null)!
            .Invoke(vm, new object?[] { "HasResult" });
    }
}
