using CAP_Core.Grid;
using CAP_Core.LightCalculation.TimeDomainSimulation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Analysis.AnalysisOutput;
using CAP.Avalonia.ViewModels.Analysis.TimeTrace;
using CAP.Avalonia.ViewModels.Canvas;
using OxyPlot;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace CAP.Avalonia.ViewModels.Analysis;

/// <summary>
/// ViewModel for the time-domain (transient) simulation panel.
/// Sweeps S-parameters over a wavelength grid, performs IFFT to obtain impulse
/// responses, convolves with a user-selected input pulse, and reports output traces.
/// </summary>
public partial class TimeDomainViewModel : ObservableObject
{
    [ObservableProperty]
    private double _centerWavelengthNm = 1550;

    [ObservableProperty]
    private double _spanNm = 100;

    [ObservableProperty]
    private int _freqPoints = 256;

    [ObservableProperty]
    private double _pulseCenterPs = 2.0;

    [ObservableProperty]
    private double _pulseSigmaPs = 0.5;

    /// <summary>
    /// Signal-source selection + parameters (issue #600): Gaussian pulse
    /// (default, back-compat), CW, or PRBS-NRZ on a signal-driven time grid.
    /// </summary>
    public TransientSourceSettingsViewModel Source { get; } = new();

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string _resultText = "";

    /// <summary>
    /// OxyPlot model for the transient waveform plot (power vs time, one series
    /// per output pin). Bound to the panel's <c>PlotView</c>. Reuses the ONA
    /// charting approach (#526) — zoom/pan come from OxyPlot's default axes.
    /// </summary>
    [ObservableProperty]
    private PlotModel _plotModel = TimeTracePlotBuilder.CreateEmptyPlotModel();

    /// <summary>
    /// Legend / per-pin visibility toggles. Each entry maps to one output-pin
    /// trace; toggling <see cref="TimeTraceSeriesViewModel.IsVisible"/> rebuilds
    /// the plot.
    /// </summary>
    public ObservableCollection<TimeTraceSeriesViewModel> Series { get; } = new();

    /// <summary>True once a completed transient result is available to plot/export.</summary>
    public bool HasResult => _lastResult != null;

    private readonly CAP_Core.ErrorConsoleService? _errorConsole;
    private DesignCanvasViewModel? _canvas;
    private TimeDomainResult? _lastResult;
    private Dictionary<Guid, string> _pinNameMap = new();

    /// <summary>Initializes a new instance of <see cref="TimeDomainViewModel"/>.</summary>
    /// <param name="errorConsole">Optional service for error logging.</param>
    public TimeDomainViewModel(CAP_Core.ErrorConsoleService? errorConsole = null)
    {
        _errorConsole = errorConsole;
    }

    /// <summary>File dialog service for CSV export. Set by MainViewModel.</summary>
    public Services.IFileDialogService? FileDialogService { get; set; }

    /// <summary>
    /// Callback that activates the canvas analysis-output picker (#754). Invoked when a
    /// run is ambiguous (several possible outputs, none designated) or the designation
    /// became invalid, so the user can pick instead of facing a modal dialog. Wired by
    /// <c>MainViewModel</c>.
    /// </summary>
    public Action? RequestOutputPicker { get; set; }

    /// <summary>Configures the panel with the current canvas context.</summary>
    public void Configure(DesignCanvasViewModel? canvas)
    {
        _canvas = canvas;
        ResultText = "";
        StatusText = "";
        _lastResult = null;
        ClearPlot();
    }

    /// <summary>Runs the time-domain simulation pipeline.</summary>
    [RelayCommand]
    private async Task RunTransient()
    {
        if (_canvas == null || _canvas.Components.Count == 0)
        {
            StatusText = LocalizationService.Instance.Translate("Analysis.Common.NoCircuit");
            return;
        }

        // All lasers off means there is no input signal at all — say so instead of
        // rendering an empty plot with status "Done" (#690, mirrors the eye analysis).
        if (_canvas.Components.Where(c => c.IsLightSource).All(c => c.IsLaserOff)
            && _canvas.Components.Any(c => c.IsLightSource))
        {
            StatusText = LocalizationService.Instance.Translate("Analysis.Common.NoLaserOn");
            return;
        }

        if (IsRunning) return;

        // Resolve the analysis output BEFORE simulating (#754): an invalid designation
        // aborts with a clear warning instead of silently guessing. Without a
        // designation every off-laser coupler counts as an output (field wish, round 4
        // final) — the eyedropper only restricts, so no picker mode is forced here.
        var resolution = AnalysisOutputResolver.Resolve(_canvas!);
        if (ReportInvalidDesignation(resolution)) return;

        IsRunning = true;
        StatusText = LocalizationService.Instance.Translate("Analysis.TimeDomain.BuildingImpulseResponses");
        ResultText = "";
        _lastResult = null;
        ClearPlot();

        try
        {
            _pinNameMap = BuildPinNameMap();
            var result = await Task.Run(() => RunSimulationCore());
            var (displayed, statusOverride) = ApplyOutputSelection(result, resolution);
            if (displayed == null)
            {
                StatusText = statusOverride!;
                return;
            }
            _lastResult = displayed;
            ResultText = TimeDomainResultFormatter.FormatResult(displayed);
            BuildPlot(displayed);
            OnPropertyChanged(nameof(HasResult));
            StatusText = statusOverride ?? string.Format(
                LocalizationService.Instance.Translate("Analysis.TimeDomain.DonePins"), displayed.PinTraces.Count);
        }
        catch (CAP_Core.LightCalculation.NonConvergentCircuitException ex)
        {
            // Physics-integrity abort (non-passive data, resonant loop, fabricated
            // energy): render the structured diagnostics fully localized.
            _errorConsole?.LogError($"Time-domain simulation blocked: {ex.Message}", ex);
            StatusText = NonConvergentCircuitMessageFormatter.Format(ex);
        }
        catch (InvalidOperationException ex)
        {
            StatusText = string.Format(
                LocalizationService.Instance.Translate("Analysis.TimeDomain.CannotRun"), ex.Message);
            _errorConsole?.LogError($"Time-domain simulation blocked: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _errorConsole?.LogError($"Time-domain simulation failed: {ex.Message}", ex);
            StatusText = string.Format(
                LocalizationService.Instance.Translate("Analysis.Common.Failed"), ex.Message);
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>Exports the last simulation result to a CSV file.</summary>
    [RelayCommand]
    private async Task ExportCsv()
    {
        if (_lastResult == null) return;

        try
        {
            string? path = null;
            if (FileDialogService != null)
            {
                path = await FileDialogService.ShowSaveFileDialogAsync(
                    "Export Time-Domain Traces",
                    "csv",
                    "CSV Files|*.csv|All Files|*.*");
            }

            if (path == null)
            {
                StatusText = LocalizationService.Instance.Translate("Analysis.Common.ExportCancelled");
                return;
            }

            var csv = TimeDomainResultFormatter.BuildCsvContent(_lastResult);
            await File.WriteAllTextAsync(path, csv);
            StatusText = string.Format(
                LocalizationService.Instance.Translate("Analysis.Common.ExportedTo"), Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            _errorConsole?.LogError($"CSV export failed: {ex.Message}", ex);
            StatusText = string.Format(
                LocalizationService.Instance.Translate("Analysis.Common.ExportFailed"), ex.Message);
        }
    }

    /// <summary>
    /// Surfaces an invalid designation (#754) as a status warning and activates the
    /// picker. Returns true when the run must not proceed.
    /// </summary>
    private bool ReportInvalidDesignation(AnalysisOutputResolution resolution)
    {
        if (resolution.State == AnalysisOutputState.DesignatedMissing)
        {
            StatusText = LocalizationService.Instance.Translate("Analysis.Output.DesignatedMissing");
            RequestOutputPicker?.Invoke();
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

    /// <summary>
    /// Applies the output designation to a completed result (#754): with a valid
    /// designation only THAT coupler's light-pin traces are displayed (null result +
    /// error status when no light reaches it); an ambiguous design keeps all traces
    /// but carries an explicit warning instead of a silent "Done".
    /// </summary>
    private static (TimeDomainResult? Displayed, string? StatusOverride) ApplyOutputSelection(
        TimeDomainResult result, AnalysisOutputResolution resolution)
    {
        if (resolution.State == AnalysisOutputState.MultipleCandidates)
            return (result, LocalizationService.Instance.Translate("Analysis.Output.MultipleCandidatesWarning"));
        if (resolution.State != AnalysisOutputState.DesignatedValid)
            return (result, null);

        var pinIds = AnalysisOutputResolver.CollectLightPinIds(resolution.Output!);
        var traces = result.PinTraces
            .Where(kv => pinIds.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        if (traces.Count == 0)
            return (null, string.Format(
                LocalizationService.Instance.Translate("Analysis.Output.NoSignal"), resolution.Output!.Name));
        return (new TimeDomainResult(result.TimeAxis, traces), null);
    }

    private TimeDomainResult RunSimulationCore()
    {
        // Tolerated measurement noise (≤ 0.5 % passivity excess in shipped measured
        // data) surfaces as a console warning; the run continues (review finding [1]).
        var (simulator, portManager) = TransientCircuitFactory.Create(
            _canvas!, warning => _errorConsole?.LogWarning(warning.ToMessage()));

        var timeDef = Source.CreateGrid(CenterWavelengthNm, SpanNm, FreqPoints);

        var inputSignals = BuildInputSignals(portManager, timeDef);
        // Laser phase noise (#834): finite-linewidth sources random-walk their phase.
        var phaseNoise = TransientCircuitFactory.BuildPhaseNoiseSettings(_canvas!);
        return simulator.Run(inputSignals, timeDef, CenterWavelengthNm, SpanNm, FreqPoints, phaseNoise);
    }

    private Dictionary<Guid, double[]> BuildInputSignals(
        PhysicalExternalPortManager portManager, TimeSignalDefinition timeDef)
    {
        double sigmaSeconds = PulseSigmaPs * 1e-12;
        double centerInput = PulseCenterPs * 1e-12;
        double pulseCenter = Math.Max(centerInput, 3 * sigmaSeconds);

        var signals = new Dictionary<Guid, double[]>();
        foreach (var usedInput in portManager.GetUsedExternalInputs())
        {
            double amplitude = Math.Sqrt(usedInput.Input.InFlowPower.Magnitude);
            var source = Source.CreateSource(amplitude, pulseCenter, sigmaSeconds);
            signals[usedInput.AttachedComponentPinId] = source.Generate(timeDef);
        }
        return signals;
    }

    /// <summary>
    /// Maps each logical pin Guid (both in- and out-flow) to a "Component.Pin" label so the
    /// plot legend shows readable names instead of Guids. Built on the UI thread from the
    /// current canvas before the simulation runs.
    /// </summary>
    private Dictionary<Guid, string> BuildPinNameMap()
    {
        var map = new Dictionary<Guid, string>();
        if (_canvas == null) return map;

        foreach (var compVm in _canvas.Components)
        {
            var component = compVm.Component;
            var componentName = component.HumanReadableName ?? component.Identifier;
            foreach (var pin in component.PhysicalPins)
            {
                if (pin.LogicalPin == null) continue;
                var label = $"{componentName}.{pin.Name}";
                // The result keys traces by the output pin's flow id; map both so the
                // label resolves regardless of which flow direction the trace carries.
                map[pin.LogicalPin.IDInFlow] = label;
                map[pin.LogicalPin.IDOutFlow] = label;
            }
        }
        return map;
    }

    /// <summary>
    /// Builds the legend items and plot model from a completed result, and subscribes to
    /// each series so toggling its visibility rebuilds the plot.
    /// </summary>
    private void BuildPlot(TimeDomainResult result)
    {
        DetachSeries();
        Series.Clear();

        var items = TimeTracePlotBuilder.BuildSeriesItems(
            result, pinId => _pinNameMap.TryGetValue(pinId, out var name) ? name : null);
        foreach (var item in items)
        {
            item.PropertyChanged += OnSeriesVisibilityChanged;
            Series.Add(item);
        }

        PlotModel = TimeTracePlotBuilder.BuildPlotModel(result, items);
    }

    /// <summary>Resets the plot and legend to the empty state (e.g. on reconfigure or re-run).</summary>
    private void ClearPlot()
    {
        DetachSeries();
        Series.Clear();
        PlotModel = TimeTracePlotBuilder.CreateEmptyPlotModel();
        OnPropertyChanged(nameof(HasResult));
    }

    private void DetachSeries()
    {
        foreach (var series in Series)
            series.PropertyChanged -= OnSeriesVisibilityChanged;
    }

    private void OnSeriesVisibilityChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TimeTraceSeriesViewModel.IsVisible) && _lastResult != null)
            PlotModel = TimeTracePlotBuilder.BuildPlotModel(_lastResult, Series);
    }
}
