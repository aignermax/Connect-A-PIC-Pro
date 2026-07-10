using System.Numerics;
using CAP_Core;
using CAP_Core.Analysis.OnaAnalysis;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.Components.Core;
using CAP_Core.ExternalPorts;
using CAP_Core.Grid;
using CAP_Core.LightCalculation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using CAP.Avalonia.Controls.Plotting;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;

namespace CAP.Avalonia.ViewModels.Analysis.OnaAnalysis;

/// <summary>
/// ViewModel for the ONA (Optical Network Analyzer) wavelength-sweep panel.
/// Sweeps the simulation across a wavelength range and plots insertion loss vs wavelength.
/// </summary>
public partial class OnaSweepViewModel : ObservableObject
{
    [ObservableProperty] private int _startNm = 1500;
    [ObservableProperty] private int _endNm = 1600;
    [ObservableProperty] private int _stepCount = 100;
    [ObservableProperty] private bool _isSweeping;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _warningText = "";
    [ObservableProperty] private PlotModel _plotModel = CreateEmptyPlotModel();

    /// <summary>True when a completed sweep result is available to export.</summary>
    public bool HasResult => _lastResult != null;

    /// <summary>
    /// The ONA Analyzer component this sweep is bound to. When set, the sweep
    /// uses the analyzer's <c>source</c> pin as the light input and treats the
    /// remaining pins as measurement points. When null, the sweep falls back
    /// to the legacy canvas-wide light-source detection.
    /// </summary>
    public Component? Analyzer { get; set; }

    /// <summary>Title shown in the tool-window header.</summary>
    public string WindowTitle => Analyzer != null
        ? $"ONA Sweep — {(string.IsNullOrEmpty(Analyzer.HumanReadableName) ? Analyzer.Name : Analyzer.HumanReadableName)}"
        : "ONA Sweep";

    private readonly ErrorConsoleService? _errorConsole;
    private DesignCanvasViewModel? _canvas;
    private WavelengthSweepResult? _lastResult;
    private CancellationTokenSource? _sweepCts;
    private Dictionary<Guid, string> _pinNameMap = new();

    /// <summary>File dialog service for CSV export. Set by MainViewModel.</summary>
    public Services.IFileDialogService? FileDialogService { get; set; }

    /// <summary>
    /// Delegate that opens an <c>OnaAnalyzerWindow</c> for the given analyzer
    /// component. Wired up by MainWindow on startup; null in headless / test
    /// scenarios, in which case <see cref="OpenWindowCommand"/> is a no-op.
    /// </summary>
    public Func<Component, Task>? OpenWindowAsync { get; set; }

    /// <summary>Initializes a new instance of <see cref="OnaSweepViewModel"/>.</summary>
    public OnaSweepViewModel(ErrorConsoleService? errorConsole = null)
    {
        _errorConsole = errorConsole;
    }

    /// <summary>Provides the canvas reference needed to build the simulation grid.</summary>
    public void Configure(DesignCanvasViewModel canvas)
    {
        _canvas = canvas;
    }

    /// <summary>Runs a wavelength sweep and updates the insertion-loss plot.</summary>
    [RelayCommand]
    private async Task RunSweep()
    {
        if (_canvas == null || IsSweeping) return;

        IsSweeping = true;
        StatusText = "Preparing ONA sweep...";
        WarningText = "";
        _lastResult = null;
        OnPropertyChanged(nameof(HasResult));

        try
        {
            var config = new WavelengthSweepConfiguration(StartNm, EndNm, StepCount);
            var (gridManager, portManager) = BuildSimulationGrid();
            if (gridManager == null)
            {
                StatusText = "No components on canvas.";
                return;
            }

            // No-silent-fallback guard: if neither the selected analyzer nor any
            // canvas-wide light-source-detection turned up an input, the sweep
            // would emit a uniform -120 dB plot with no diagnostic. Bail out
            // visibly instead and surface the cause in the Error Console.
            if (portManager.GetAllExternalInputs().Count == 0)
            {
                var hint = Analyzer != null
                    ? $"ONA Analyzer '{Analyzer.Name}' source pin is not connected to a light input."
                    : "No light source found on the canvas (place a Grating Coupler / Edge Coupler, or select an ONA Analyzer and connect its source pin).";
                _errorConsole?.LogError($"ONA sweep aborted: {hint}");
                StatusText = "Aborted — no light source. See Error Console.";
                return;
            }

            var builder = new SystemMatrixBuilder(gridManager);
            var sweeper = new WavelengthSweeper(builder, portManager);
            _sweepCts?.Dispose();
            _sweepCts = new CancellationTokenSource();

            StatusText = $"Running ONA sweep ({StepCount} steps)...";
            _lastResult = await sweeper.RunSweepAsync(config, gridManager, _sweepCts.Token);
            OnPropertyChanged(nameof(HasResult));

            if (_lastResult.Warnings.Count > 0)
            {
                foreach (var warning in _lastResult.Warnings)
                    _errorConsole?.LogWarning(warning);
                WarningText = $"{_lastResult.Warnings.Count} warning(s) — see Error Console";
            }

            UpdatePlotModel(_lastResult);
            StatusText = $"ONA sweep complete: {_lastResult.DataPoints.Count} points";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Sweep cancelled.";
        }
        catch (Exception ex)
        {
            _errorConsole?.LogError($"ONA sweep failed: {ex.Message}", ex);
            StatusText = $"Sweep failed: {ex.Message}";
        }
        finally
        {
            IsSweeping = false;
            _sweepCts?.Dispose();
            _sweepCts = null;
        }
    }

    /// <summary>Cancels a running ONA sweep.</summary>
    [RelayCommand]
    private void CancelSweep()
    {
        _sweepCts?.Cancel();
    }

    /// <summary>
    /// Opens the ONA Analyzer tool window for the currently selected analyzer
    /// component, via the <see cref="OpenWindowAsync"/> delegate.
    /// </summary>
    [RelayCommand]
    private async Task OpenWindow()
    {
        if (OpenWindowAsync == null) return;
        var selected = _canvas?.SelectedComponent?.Component;
        if (selected == null || !selected.IsAnalysisTool)
        {
            _errorConsole?.LogWarning(
                "Select an ONA Analyzer component on the canvas before opening the sweep window.");
            return;
        }
        await OpenWindowAsync(selected);
    }

    /// <summary>Exports the last sweep result as CSV.</summary>
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
                    "Export ONA Results", "csv", "CSV Files|*.csv|All Files|*.*");
            }

            if (path == null) { StatusText = "Export cancelled"; return; }

            await File.WriteAllTextAsync(path, _lastResult.GenerateCsvContent(ResolvePinName));
            StatusText = $"Exported to {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            _errorConsole?.LogError($"ONA export failed: {ex.Message}", ex);
            StatusText = $"Export failed: {ex.Message}";
        }
    }

    private (GridManager? gridManager, PhysicalExternalPortManager portManager) BuildSimulationGrid()
    {
        if (_canvas == null || _canvas.Components.Count == 0) return (null, new PhysicalExternalPortManager());

        var tileManager = new ComponentListTileManager();
        foreach (var compVm in _canvas.Components)
            tileManager.AddComponent(compVm.Component);

        var portManager = new PhysicalExternalPortManager();
        _pinNameMap = new Dictionary<Guid, string>();
        ConfigureLightSourcesAndBuildPinMap(portManager, _pinNameMap);

        var gridManager = GridManager.CreateForSimulation(
            tileManager, _canvas.ConnectionManager, portManager);
        return (gridManager, portManager);
    }

    private void ConfigureLightSourcesAndBuildPinMap(
        PhysicalExternalPortManager portManager,
        Dictionary<Guid, string> pinNameMap)
    {
        if (_canvas == null) return;

        var allComponents = SimulationService.GetAllComponentsRecursively(_canvas.Components);

        // Always build the GUID → readable name map across the full canvas so
        // CSV columns and plot series titles look right regardless of which
        // analyzer is driving the sweep.
        foreach (var component in allComponents)
        {
            var displayName = !string.IsNullOrEmpty(component.HumanReadableName)
                ? component.HumanReadableName!
                : component.Name;

            foreach (var pin in component.PhysicalPins)
            {
                if (pin.LogicalPin == null) continue;
                var pinLabel = $"{displayName}.{pin.Name}";
                // Distinguish the two light-flow directions so CSV columns and plot
                // series aren't ambiguous duplicates: IDInFlow is light arriving at
                // the pin, IDOutFlow is light leaving it.
                pinNameMap[pin.LogicalPin.IDInFlow] = $"{pinLabel} (in)";
                pinNameMap[pin.LogicalPin.IDOutFlow] = $"{pinLabel} (out)";
            }
        }

        // Source selection:
        //   • If an Analyzer is set, use ONLY its "source" pin as input.
        //   • Otherwise fall back to the L-key heuristic (grating / edge coupler).
        if (Analyzer != null)
        {
            var sourcePin = Analyzer.PhysicalPins.FirstOrDefault(
                p => string.Equals(p.Name, "source", StringComparison.OrdinalIgnoreCase));
            if (sourcePin?.LogicalPin?.MatterType != MatterType.Light)
            {
                _errorConsole?.LogError(
                    $"ONA Analyzer '{Analyzer.Name}' has no usable 'source' pin. " +
                    "Add a pin named 'source' to the analyzer or connect the analyzer's source pin to the device-under-test.");
                return;
            }
            var input = new ExternalInput(
                $"ona_{Analyzer.Identifier}_source",
                LaserType.Red, 0, new Complex(1.0, 0));
            portManager.AddLightSource(input, sourcePin.LogicalPin.IDInFlow);
            return;
        }

        foreach (var component in allComponents)
        {
            if (!SimulationService.IsLightSource(component)) continue;
            // Laser off = listen-only output coupler (#690) — inject no sweep light.
            if (!component.LaserEnabled) continue;
            foreach (var pin in component.PhysicalPins)
            {
                if (pin.LogicalPin?.MatterType != MatterType.Light) continue;
                var input = new ExternalInput(
                    $"ona_{component.Identifier}_{pin.Name}",
                    LaserType.Red, 0, new Complex(1.0, 0));
                portManager.AddLightSource(input, pin.LogicalPin.IDInFlow);
            }
        }
    }

    private string? ResolvePinName(Guid pinId)
        => _pinNameMap.TryGetValue(pinId, out var name) ? name : null;

    private void UpdatePlotModel(WavelengthSweepResult result)
    {
        var model = CreateEmptyPlotModel();
        var wavelengths = result.GetWavelengthValues();

        // Decide which pins to plot:
        //   • Analyzer mode: only the analyzer's measurement pin(s) — that's
        //     the explicit user intent ("measure here").
        //   • Fallback (no analyzer): every monitored pin, with the noise-
        //     floor lines kept visible so the user can see SOMETHING was
        //     measured rather than facing a blank chart.
        var measurementPinIds = ResolveMeasurementPinIds(result);

        int seriesCount = 0;
        bool anyAboveFloor = false;
        foreach (var pinId in measurementPinIds)
        {
            var losses = result.GetInsertionLossSeriesForPin(pinId);
            if (losses.Any(v => v > WavelengthDataPoint.MinInsertionLossDb + 1))
                anyAboveFloor = true;

            var seriesTitle = ResolvePinName(pinId) ?? $"Pin {pinId.ToString("N")[..6]}";
            var series = new XTrackingLineSeries
            {
                Title = seriesTitle,
                StrokeThickness = 1.5,
                CanTrackerInterpolatePoints = true,
                TrackerTextProvider = dp => $"{seriesTitle}\nλ = {dp.X:0} nm\nIL = {dp.Y:0.00} dB",
            };

            for (int i = 0; i < wavelengths.Length; i++)
                series.Points.Add(new DataPoint(wavelengths[i], losses[i]));

            model.Series.Add(series);
            if (++seriesCount >= 8) break; // cap series to prevent plot overload
        }

        // No-silent-fallback diagnostic: a sweep that produced N series, all
        // pinned at the −120 dB floor, almost certainly means the analyzer's
        // measurement pin never received any light. Tell the user that
        // explicitly instead of letting them stare at a flat line.
        if (model.Series.Count > 0 && !anyAboveFloor)
        {
            var hint = Analyzer != null
                ? $"All output series for analyzer '{Analyzer.Name}' are at the −120 dB floor. Connect the analyzer's 'source' pin to your device input and its 'measurement' pin to the device output, then run again."
                : "All output series are at the −120 dB floor. No light reached any measurement pin — check that the path from light source to output is connected through your design.";
            _errorConsole?.LogWarning(hint);
            StatusText = "Sweep complete — all outputs at noise floor (see Error Console).";
        }

        model.InvalidatePlot(true);
        PlotModel = model;
    }

    /// <summary>
    /// Returns the pin IDs to draw. With an analyzer set, returns the
    /// pin IDs of its non-source pins (the measurement ports). Without one,
    /// returns every monitored pin so the legacy heuristic path still
    /// produces something visible.
    /// </summary>
    private IReadOnlyList<Guid> ResolveMeasurementPinIds(WavelengthSweepResult result)
    {
        if (Analyzer == null) return result.MonitoredPinIds.ToList();

        var measurementIds = new List<Guid>();
        var monitored = new HashSet<Guid>(result.MonitoredPinIds);
        foreach (var pin in Analyzer.PhysicalPins)
        {
            if (pin.LogicalPin == null) continue;
            if (string.Equals(pin.Name, "source", StringComparison.OrdinalIgnoreCase)) continue;
            if (monitored.Contains(pin.LogicalPin.IDInFlow))
                measurementIds.Add(pin.LogicalPin.IDInFlow);
        }
        // Fall back to the full monitor list if the analyzer's measurement
        // pins aren't in the simulation result — better to show too much
        // than nothing.
        return measurementIds.Count > 0 ? measurementIds : result.MonitoredPinIds.ToList();
    }

    // Light colors so title, axes, ticks and gridlines are readable on the
    // dark (#1e1e1e) tool-window background instead of near-invisible black.
    private static readonly OxyColor PlotForeground = OxyColor.Parse("#E0E0E0");
    private static readonly OxyColor PlotGridline = OxyColor.Parse("#404040");
    private static readonly OxyColor PlotAxisline = OxyColor.Parse("#808080");

    private static PlotModel CreateEmptyPlotModel()
    {
        var model = new PlotModel
        {
            Title = "ONA — Insertion Loss",
            Background = OxyColors.Transparent,
            TextColor = PlotForeground,
            TitleColor = PlotForeground,
            PlotAreaBorderColor = PlotAxisline,
        };
        model.Axes.Add(CreateAxis(AxisPosition.Bottom, "Wavelength (nm)"));
        model.Axes.Add(CreateAxis(AxisPosition.Left, "Insertion Loss (dB)"));
        return model;
    }

    private static LinearAxis CreateAxis(AxisPosition position, string title) => new()
    {
        Position = position,
        Title = title,
        MajorGridlineStyle = LineStyle.Dot,
        MajorGridlineColor = PlotGridline,
        TextColor = PlotForeground,
        TitleColor = PlotForeground,
        TicklineColor = PlotAxisline,
        AxislineColor = PlotAxisline,
        AxislineStyle = LineStyle.Solid,
    };
}
