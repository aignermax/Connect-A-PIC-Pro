using System.Globalization;
using CAP_Core.Analysis.EyeDiagram;
using CAP_Core.LightCalculation.TimeDomainSimulation;
using CAP.Avalonia.ViewModels.Canvas;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OxyPlot;

namespace CAP.Avalonia.ViewModels.Analysis.EyeDiagram;

/// <summary>
/// ViewModel for the eye-diagram / BER panel (#535). Drives a PRBS-modulated
/// transient simulation (#527 pipeline), folds the output-coupler trace (the
/// coupler with its laser off, #690; strongest trace as legacy fallback) into
/// an eye histogram, and reports Q-factor / BER with a receiver noise model.
/// </summary>
public partial class EyeDiagramViewModel : ObservableObject
{
    private const double GigabitsToBits = 1e9;
    private const double SecondsToPicoseconds = 1e12;

    /// <summary>Receiver electrical bandwidth as a fraction of the bit rate (typical NRZ receiver).</summary>
    private const double ReceiverBandwidthFactor = 0.75;

    private const double CenterWavelengthNm = TimeDomainSimulator.DefaultCenterWavelengthNm;
    private const double SpanNm = TimeDomainSimulator.DefaultSpanNm;
    private const int FreqPoints = TimeDomainSimulator.DefaultNPoints;

    /// <summary>Selectable PRBS pattern orders.</summary>
    public IReadOnlyList<PrbsOrder> PrbsOrders { get; } =
        new[] { PrbsOrder.Prbs7, PrbsOrder.Prbs11, PrbsOrder.Prbs23 };

    [ObservableProperty]
    private double _bitRateGbps = 25;

    [ObservableProperty]
    private PrbsOrder _selectedPrbsOrder = PrbsOrder.Prbs7;

    /// <summary>Decision threshold as a fraction of the trace amplitude range (0…1).</summary>
    [ObservableProperty]
    private double _thresholdRelative = 0.5;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusText = "";

    /// <summary>Formatted eye metrics (Q, BER, height, width, jitter) shown beside the plot.</summary>
    [ObservableProperty]
    private string _metricsText = "";

    /// <summary>OxyPlot heat-map model of the eye persistence display.</summary>
    [ObservableProperty]
    private PlotModel _plotModel = EyeDiagramPlotBuilder.CreateEmptyPlotModel();

    /// <summary>True once a completed eye analysis is available to plot/export.</summary>
    public bool HasResult => _lastHistogram != null;

    /// <summary>File dialog service for CSV export. Set by MainViewModel.</summary>
    public Services.IFileDialogService? FileDialogService { get; set; }

    private readonly CAP_Core.ErrorConsoleService? _errorConsole;
    private DesignCanvasViewModel? _canvas;
    private EyeHistogram? _lastHistogram;

    /// <summary>Initializes a new instance of <see cref="EyeDiagramViewModel"/>.</summary>
    /// <param name="errorConsole">Optional service for error logging.</param>
    public EyeDiagramViewModel(CAP_Core.ErrorConsoleService? errorConsole = null)
    {
        _errorConsole = errorConsole;
    }

    /// <summary>Configures the panel with the current canvas context.</summary>
    /// <param name="canvas">Canvas providing components and connections.</param>
    public void Configure(DesignCanvasViewModel? canvas)
    {
        _canvas = canvas;
        StatusText = "";
        MetricsText = "";
        _lastHistogram = null;
        PlotModel = EyeDiagramPlotBuilder.CreateEmptyPlotModel();
        OnPropertyChanged(nameof(HasResult));
    }

    /// <summary>Runs the PRBS transient simulation and updates the eye plot and metrics.</summary>
    [RelayCommand]
    private async Task RunEyeAnalysis()
    {
        if (_canvas == null || _canvas.Components.Count == 0)
        {
            StatusText = "No circuit loaded.";
            return;
        }
        if (IsRunning) return;

        IsRunning = true;
        StatusText = "Running PRBS transient simulation…";
        MetricsText = "";
        _lastHistogram = null;

        try
        {
            var outcome = await Task.Run(RunAnalysisCore);
            if (outcome.Error != null || outcome.Histogram == null)
            {
                // Expected "can't run" conditions (no light source / no output traces) are surfaced
                // as a status, not thrown — so they don't break under the debugger (#535/#570).
                StatusText = outcome.Error ?? "Eye analysis produced no result.";
                return;
            }
            _lastHistogram = outcome.Histogram;
            PlotModel = EyeDiagramPlotBuilder.BuildPlotModel(outcome.Histogram);
            MetricsText = FormatMetrics(outcome.Metrics!);   // non-null when Histogram != null
            OnPropertyChanged(nameof(HasResult));
            StatusText = outcome.Warning ?? "Done";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _errorConsole?.LogError($"Eye-diagram analysis failed: {ex.Message}", ex);
            StatusText = $"Failed: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>Exports the eye histogram to a CSV file.</summary>
    [RelayCommand]
    private async Task ExportCsv()
    {
        if (_lastHistogram == null) return;

        try
        {
            string? path = null;
            if (FileDialogService != null)
            {
                path = await FileDialogService.ShowSaveFileDialogAsync(
                    "Export Eye Histogram", "csv", "CSV Files|*.csv|All Files|*.*");
            }
            if (path == null)
            {
                StatusText = "Export cancelled";
                return;
            }

            await File.WriteAllTextAsync(path, _lastHistogram.ToCsv());
            StatusText = $"Exported to {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            _errorConsole?.LogError($"Eye histogram export failed: {ex.Message}", ex);
            StatusText = $"Export failed: {ex.Message}";
        }
    }

    /// <summary>Outcome of an eye run: either a result (optionally with a non-fatal
    /// <see cref="Warning"/>), or a user-facing <see cref="Error"/> for an expected
    /// "can't run" condition (no light source / no output traces).</summary>
    private sealed record EyeRunOutcome(
        EyeHistogram? Histogram, EyeMetrics? Metrics, string? Error, string? Warning = null);

    private EyeRunOutcome RunAnalysisCore()
    {
        var (simulator, ports) = TransientCircuitFactory.Create(_canvas!);
        var outputPinIds = TransientCircuitFactory.CollectOutputCouplerPinIds(_canvas!);

        double bitRateHz = BitRateGbps * GigabitsToBits;
        var sweepDef = TimeSignalDefinition.FromWavelengthSweep(CenterWavelengthNm, SpanNm, FreqPoints);
        var plan = EyeSimulationPlan.Create(
            bitRateHz, sweepDef.SampleRateHz, PrbsGenerator.PatternLength(SelectedPrbsOrder));

        var bits = PrbsGenerator.GenerateBits(SelectedPrbsOrder, plan.BitCount);
        var timeDef = new TimeSignalDefinition(sweepDef.SampleRateHz, plan.TotalSamples);

        var signals = new Dictionary<Guid, double[]>();
        foreach (var used in ports.GetUsedExternalInputs())
        {
            double amplitude = Math.Sqrt(used.Input.InFlowPower.Magnitude);
            signals[used.AttachedComponentPinId] = PrbsGenerator.ToNrzSamples(bits, plan.SamplesPerBit, amplitude);
        }
        if (signals.Count == 0)
            return new EyeRunOutcome(null, null, outputPinIds.Count > 0
                ? "No laser is switched on — turn the laser on at your input coupler."
                : "No light source found — place an input coupler (e.g. a grating/edge coupler).");

        var result = simulator.Run(signals, timeDef, CenterWavelengthNm, SpanNm, FreqPoints);
        if (result.PinTraces.Count == 0)
            return new EyeRunOutcome(null, null,
                "The circuit produced no output traces — connect an output path from the light source to a detector/output.");

        // Analyse the trace at a coupler whose laser is off (a true output, #690);
        // fall back to the strongest trace (with a warning) for all-lasers-on designs.
        var selection = EyeTraceSelector.Select(result, outputPinIds);
        if (selection.Trace == null)
            return new EyeRunOutcome(null, null, selection.Error);
        var trace = selection.Trace;

        // No time bin can be finer than one sample, otherwise bins stay empty.
        int timeBins = Math.Min(EyeDiagramBuilder.DefaultTimeBins, plan.SamplesPerBit);
        var histogram = EyeDiagramBuilder.Build(
            trace, timeDef.SampleRateHz, plan.BitPeriodSeconds, timeBins);
        double threshold = histogram.MinAmplitude
            + ThresholdRelative * (histogram.MaxAmplitude - histogram.MinAmplitude);
        var noise = new NoiseModel { BandwidthHz = ReceiverBandwidthFactor * bitRateHz };
        var metrics = BerEstimator.Estimate(
            trace, timeDef.SampleRateHz, plan.BitPeriodSeconds, threshold, noise, timeBins);

        return new EyeRunOutcome(histogram, metrics, null, selection.Warning);
    }

    private static string FormatMetrics(EyeMetrics metrics)
    {
        var inv = CultureInfo.InvariantCulture;
        return string.Join(Environment.NewLine,
            string.Format(inv, "Q factor:       {0:F2}", metrics.QFactor),
            string.Format(inv, "BER (est.):     {0:E2}", metrics.BerEstimate),
            string.Format(inv, "Eye height:     {0:E3}", metrics.EyeHeight),
            string.Format(inv, "Eye width:      {0:F2} ps", metrics.EyeWidthSeconds * SecondsToPicoseconds),
            string.Format(inv, "RMS jitter:     {0:F3} ps", metrics.RmsJitterSeconds * SecondsToPicoseconds),
            string.Format(inv, "Optimal sample: {0:F2} ps", metrics.OptimalSampleOffsetSeconds * SecondsToPicoseconds));
    }
}
