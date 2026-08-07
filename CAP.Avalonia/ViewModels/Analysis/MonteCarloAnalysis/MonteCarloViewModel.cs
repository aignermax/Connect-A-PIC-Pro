using System.Globalization;
using CAP_Core.Analysis.MonteCarloAnalysis;
using CAP_Core.Analysis.OnaAnalysis;
using CAP_Core.Components.Core;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Analysis.AnalysisOutput;
using CAP.Avalonia.ViewModels.Canvas;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OxyPlot;

namespace CAP.Avalonia.ViewModels.Analysis.MonteCarloAnalysis;

/// <summary>
/// ViewModel for the Monte-Carlo fabrication-variance tab (#818): jitters all
/// component parameters with Gaussian noise, re-simulates N times, and shows
/// either the spectral envelope over the nominal curve or the eye-openness
/// distribution — a statistical yield view of the design.
/// </summary>
public partial class MonteCarloViewModel : ObservableObject
{
    private const double PercentToFraction = 0.01;

    /// <summary>Selectable per-run metrics.</summary>
    public IReadOnlyList<MonteCarloMetricOption> Metrics { get; } = new[]
    {
        new MonteCarloMetricOption(
            MonteCarloMetric.SpectrumEnvelope,
            LocalizationService.Instance.Translate("MonteCarlo.MetricSpectrum")),
        new MonteCarloMetricOption(
            MonteCarloMetric.EyeOpenness,
            LocalizationService.Instance.Translate("MonteCarlo.MetricEye")),
    };

    [ObservableProperty] private MonteCarloMetricOption _selectedMetric;
    [ObservableProperty] private int _runCount = MonteCarloConfiguration.DefaultRunCount;
    [ObservableProperty] private double _sigmaPercent = MonteCarloConfiguration.DefaultSigmaRelative / PercentToFraction;
    [ObservableProperty] private int _seed = MonteCarloConfiguration.DefaultSeed;
    [ObservableProperty] private int _startNm = 1500;
    [ObservableProperty] private int _endNm = 1600;
    [ObservableProperty] private int _stepCount = 21;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private PlotModel _plotModel;

    /// <summary>True once a completed Monte-Carlo result is shown.</summary>
    public bool HasResult => !string.IsNullOrEmpty(SummaryText);

    /// <summary>True when the spectrum metric is selected (shows the wavelength-range inputs).</summary>
    public bool IsSpectrumMetric => SelectedMetric.Metric == MonteCarloMetric.SpectrumEnvelope;

    private readonly CAP_Core.ErrorConsoleService? _errorConsole;
    private DesignCanvasViewModel? _canvas;
    private CancellationTokenSource? _runCts;

    /// <summary>Initializes a new instance of <see cref="MonteCarloViewModel"/>.</summary>
    /// <param name="errorConsole">Optional service for error logging.</param>
    public MonteCarloViewModel(CAP_Core.ErrorConsoleService? errorConsole = null)
    {
        _errorConsole = errorConsole;
        _selectedMetric = Metrics[0];
        _plotModel = MonteCarloPlotBuilder.CreateEmptyModel("Monte Carlo");
    }

    /// <summary>Provides the canvas reference needed to build the simulation circuit.</summary>
    public void Configure(DesignCanvasViewModel canvas) => _canvas = canvas;

    partial void OnSelectedMetricChanged(MonteCarloMetricOption value)
        => OnPropertyChanged(nameof(IsSpectrumMetric));

    partial void OnSummaryTextChanged(string value)
        => OnPropertyChanged(nameof(HasResult));

    /// <summary>Runs the Monte-Carlo fabrication-variance analysis.</summary>
    [RelayCommand]
    private async Task RunAnalysis()
    {
        if (IsRunning) return;
        if (_canvas == null || _canvas.Components.Count == 0)
        {
            StatusText = LocalizationService.Instance.Translate("Analysis.Common.NoCircuit");
            return;
        }

        var sliders = CollectSliders();
        if (sliders.Count == 0)
        {
            StatusText = LocalizationService.Instance.Translate("Analysis.Mc.NoSliders");
            return;
        }

        IsRunning = true;
        ProgressPercent = 0;
        SummaryText = "";
        _runCts?.Dispose();
        _runCts = new CancellationTokenSource();

        try
        {
            var config = new MonteCarloConfiguration(RunCount, SigmaPercent * PercentToFraction, Seed);
            var progress = new Progress<MonteCarloProgress>(p =>
            {
                ProgressPercent = 100.0 * p.CompletedRuns / p.TotalRuns;
                StatusText = string.Format(
                    LocalizationService.Instance.Translate("Analysis.Mc.Running"),
                    p.CompletedRuns, p.TotalRuns);
            });

            if (IsSpectrumMetric)
                await RunSpectrumAnalysis(config, sliders, progress, _runCts.Token);
            else
                await RunEyeAnalysis(config, sliders, progress, _runCts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText = LocalizationService.Instance.Translate("Analysis.Mc.Cancelled");
        }
        catch (CAP_Core.LightCalculation.NonConvergentCircuitException ex)
        {
            _errorConsole?.LogError($"Monte-Carlo analysis blocked: {ex.Message}", ex);
            StatusText = NonConvergentCircuitMessageFormatter.Format(ex);
        }
        catch (Exception ex)
        {
            _errorConsole?.LogError($"Monte-Carlo analysis failed: {ex.Message}", ex);
            StatusText = string.Format(
                LocalizationService.Instance.Translate("Analysis.Common.Failed"), ex.Message);
        }
        finally
        {
            IsRunning = false;
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    /// <summary>Cancels the running analysis after the current run finishes.</summary>
    [RelayCommand]
    private void CancelAnalysis() => _runCts?.Cancel();

    private async Task RunSpectrumAnalysis(
        MonteCarloConfiguration config, IReadOnlyList<Slider> sliders,
        IProgress<MonteCarloProgress> progress, CancellationToken cancellationToken)
    {
        var sweepConfig = new WavelengthSweepConfiguration(StartNm, EndNm, StepCount);
        var (sampler, error) = MonteCarloSpectrumSampler.Create(_canvas!, sweepConfig);
        if (sampler == null)
        {
            StatusText = error ?? "";
            return;
        }

        var result = await new MonteCarloRunner().RunAsync(
            config, sliders, sampler.SampleAsync, progress, cancellationToken);

        PlotModel = MonteCarloPlotBuilder.BuildEnvelopePlot(
            sampler.Wavelengths, result, sampler.SelectedPinName);
        SummaryText = FormatSpectrumSummary(result, sliders.Count);
        StatusText = string.Format(
            LocalizationService.Instance.Translate("Analysis.Mc.Complete"), config.RunCount);
    }

    private async Task RunEyeAnalysis(
        MonteCarloConfiguration config, IReadOnlyList<Slider> sliders,
        IProgress<MonteCarloProgress> progress, CancellationToken cancellationToken)
    {
        var resolution = AnalysisOutputResolver.Resolve(_canvas!);
        if (ReportInvalidDesignation(resolution)) return;

        var sampler = new MonteCarloEyeSampler(_canvas!, resolution);
        var result = await new MonteCarloRunner().RunAsync(
            config, sliders, sampler.SampleAsync, progress, cancellationToken);

        var samples = result.GetSamplesAtIndex(0);
        var histogram = DistributionHistogram.Create(samples);
        PlotModel = MonteCarloPlotBuilder.BuildHistogramPlot(histogram, result.NominalCurve[0]);
        SummaryText = FormatEyeSummary(result, samples, sliders.Count);
        StatusText = string.Format(
            LocalizationService.Instance.Translate("Analysis.Mc.Complete"), config.RunCount);
    }

    private bool ReportInvalidDesignation(AnalysisOutputResolution resolution)
    {
        if (resolution.State == AnalysisOutputState.DesignatedMissing)
        {
            StatusText = LocalizationService.Instance.Translate("Analysis.Output.DesignatedMissing");
            return true;
        }
        if (resolution.State == AnalysisOutputState.DesignatedLaserOn)
        {
            StatusText = string.Format(
                LocalizationService.Instance.Translate("Analysis.Output.DesignatedLaserOn"),
                resolution.Output!.Name);
            return true;
        }
        return false;
    }

    private List<Slider> CollectSliders()
        => SimulationService.GetAllComponentsRecursively(_canvas!.Components)
            .SelectMany(component => component.GetAllSliders())
            .ToList();

    private string FormatSpectrumSummary(MonteCarloResult result, int sliderCount)
    {
        var inv = CultureInfo.InvariantCulture;
        double worstDip = result.GetMinCurve().Min();
        double nominalMin = result.NominalCurve.Min();
        return string.Join(Environment.NewLine,
            string.Format(inv, "Jittered parameters:  {0}", sliderCount),
            string.Format(inv, "Nominal worst IL:     {0:F2} dB", nominalMin),
            string.Format(inv, "Monte-Carlo worst IL: {0:F2} dB", worstDip));
    }

    private string FormatEyeSummary(MonteCarloResult result, double[] samples, int sliderCount)
    {
        var inv = CultureInfo.InvariantCulture;
        double openFraction = samples.Count(s => s > 0) / (double)samples.Length;
        return string.Join(Environment.NewLine,
            string.Format(inv, "Jittered parameters: {0}", sliderCount),
            string.Format(inv, "Nominal eye height:  {0:E3}", result.NominalCurve[0]),
            string.Format(inv, "p5 / p50 / p95:      {0:E3} / {1:E3} / {2:E3}",
                result.GetPercentileCurve(5)[0],
                result.GetPercentileCurve(50)[0],
                result.GetPercentileCurve(95)[0]),
            string.Format(inv, "Open-eye yield:      {0:P1}", openFraction));
    }
}
