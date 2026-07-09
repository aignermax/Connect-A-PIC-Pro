using System.Numerics;
using System.Text;
using CAP_Core;
using CAP_Core.Analysis;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.ExternalPorts;
using CAP_Core.Grid;
using CAP_Core.LightCalculation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CAP.Avalonia.ViewModels.Canvas;

namespace CAP.Avalonia.ViewModels.Analysis;

/// <summary>
/// ViewModel for the parameter sweep UI panel.
/// Runs a sweep by varying a component slider across a range,
/// running simulation at each step, and collecting output power data.
/// </summary>
public partial class ParameterSweepViewModel : ObservableObject
{
    [ObservableProperty]
    private double _startValue;

    [ObservableProperty]
    private double _endValue = 360;

    [ObservableProperty]
    private int _stepCount = 20;

    [ObservableProperty]
    private bool _isSweeping;

    [ObservableProperty]
    private string _resultText = "";

    [ObservableProperty]
    private string _statusText = "";

    private readonly ErrorConsoleService? _errorConsole;
    private DesignCanvasViewModel? _canvas;
    private ComponentViewModel? _targetComponent;
    private SweepResult? _lastResult;

    /// <summary>Initializes a new instance of <see cref="ParameterSweepViewModel"/>.</summary>
    /// <param name="errorConsole">Optional service for error logging.</param>
    public ParameterSweepViewModel(ErrorConsoleService? errorConsole = null)
    {
        _errorConsole = errorConsole;
    }

    /// <summary>
    /// File dialog service for CSV export. Set by MainViewModel.
    /// </summary>
    public Services.IFileDialogService? FileDialogService { get; set; }

    /// <summary>
    /// Configures the sweep panel for the given component.
    /// Pre-fills range from the component's slider min/max.
    /// </summary>
    public void ConfigureForComponent(ComponentViewModel? component, DesignCanvasViewModel? canvas)
    {
        _canvas = canvas;
        _targetComponent = component;
        ResultText = "";
        StatusText = "";
        _lastResult = null;

        if (component != null && component.HasSliders)
        {
            StartValue = component.SliderMin;
            EndValue = component.SliderMax;
        }
    }

    [RelayCommand]
    private async Task RunSweep()
    {
        if (_canvas == null || _targetComponent == null || !_targetComponent.HasSliders)
            return;

        if (IsSweeping) return;
        IsSweeping = true;
        StatusText = "Running sweep...";
        ResultText = "";

        try
        {
            var slider = _targetComponent.Component.GetSlider(0);
            if (slider == null) return;

            double originalValue = slider.Value;
            var stepValues = GenerateSweepValues();
            var dataPoints = new List<SweepDataPoint>();

            // Resolve pin names for display
            var pinNameMap = BuildPinNameMap();

            try
            {
                for (int i = 0; i < stepValues.Length; i++)
                {
                    StatusText = $"Sweep step {i + 1}/{stepValues.Length}...";
                    slider.Value = stepValues[i];

                    var fieldResults = await RunSingleSimulation();
                    if (fieldResults != null)
                        dataPoints.Add(new SweepDataPoint(stepValues[i], fieldResults));
                }
            }
            finally
            {
                slider.Value = originalValue;
            }

            // Build SweepResult using core classes
            var parameter = new SweepParameter(_targetComponent.Component, 0,
                _targetComponent.SliderLabel);
            var config = new SweepConfiguration(parameter, StartValue, EndValue, StepCount, 1550);
            var monitoredPins = dataPoints.Count > 0
                ? dataPoints[0].OutputPowers.Keys.ToList()
                : new List<Guid>();

            _lastResult = new SweepResult(config, dataPoints, monitoredPins);

            // Format results as text table
            ResultText = FormatResultsTable(_lastResult, pinNameMap);
            StatusText = $"Sweep complete: {dataPoints.Count} points";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _errorConsole?.LogError($"Parameter sweep failed: {ex.Message}", ex);
            StatusText = $"Sweep failed: {ex.Message}";
        }
        finally
        {
            IsSweeping = false;
        }
    }

    [RelayCommand]
    private async Task ExportCsv()
    {
        if (_lastResult == null) return;

        try
        {
            var csv = SweepCsvExporter.GenerateCsvContent(_lastResult);

            string? path = null;
            if (FileDialogService != null)
            {
                var defaultName = $"sweep_{DateTime.Now:yyyyMMdd_HHmmss}";
                path = await FileDialogService.ShowSaveFileDialogAsync(
                    "Export Sweep Results",
                    "csv",
                    $"CSV Files|*.csv|All Files|*.*");
            }

            if (path == null)
            {
                StatusText = "Export cancelled";
                return;
            }

            await File.WriteAllTextAsync(path, csv);
            StatusText = $"Exported to {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            _errorConsole?.LogError($"Failed to export sweep results: {ex.Message}", ex);
            StatusText = $"Export failed: {ex.Message}";
        }
    }

    private double[] GenerateSweepValues()
    {
        var values = new double[StepCount];
        double step = (EndValue - StartValue) / (StepCount - 1);
        for (int i = 0; i < StepCount; i++)
            values[i] = StartValue + i * step;
        return values;
    }

    /// <summary>
    /// Runs the full simulation pipeline once and returns field results.
    /// Reuses SimulationService logic but returns raw field data.
    /// </summary>
    private async Task<Dictionary<Guid, Complex>?> RunSingleSimulation()
    {
        if (_canvas == null || _canvas.Components.Count == 0 || _canvas.Connections.Count == 0)
            return null;

        var tileManager = new ComponentListTileManager();
        foreach (var compVm in _canvas.Components)
            tileManager.AddComponent(compVm.Component);

        var portManager = new PhysicalExternalPortManager();
        ConfigureLightSources(portManager);

        var gridManager = GridManager.CreateForSimulation(
            tileManager, _canvas.ConnectionManager, portManager);

        var builder = new SystemMatrixBuilder(gridManager);
        var calculator = new GridLightCalculator(builder, gridManager);
        var cts = new CancellationTokenSource();
        return await calculator.CalculateFieldPropagationAsync(cts, 1550);
    }

    private void ConfigureLightSources(PhysicalExternalPortManager portManager)
    {
        if (_canvas == null) return;

        foreach (var compVm in _canvas.Components)
        {
            if (!LightSourceClassifier.IsLightInjectingCoupler(compVm.TemplateName)) continue;

            var laserConfig = compVm.LaserConfig;
            double power = laserConfig?.InputPower ?? 1.0;
            var laserType = laserConfig?.WavelengthNm == StandardWaveLengths.GreenNM
                ? LaserType.Green
                : laserConfig?.WavelengthNm == StandardWaveLengths.BlueNM
                    ? LaserType.Blue
                    : LaserType.Red;

            foreach (var pin in compVm.Component.PhysicalPins)
            {
                if (pin.LogicalPin?.MatterType != MatterType.Light) continue;
                var input = new ExternalInput(
                    $"src_{compVm.Component.Identifier}_{pin.Name}",
                    laserType, 0, new Complex(power, 0));
                portManager.AddLightSource(input, pin.LogicalPin.IDInFlow);
            }
        }
    }

    private Dictionary<Guid, string> BuildPinNameMap()
    {
        var map = new Dictionary<Guid, string>();
        if (_canvas == null) return map;

        foreach (var compVm in _canvas.Components)
        {
            foreach (var pin in compVm.Component.PhysicalPins)
            {
                if (pin.LogicalPin == null) continue;
                var shortName = $"{compVm.Component.Identifier}.{pin.Name}";
                // Truncate for table display
                if (shortName.Length > 16) shortName = shortName[..16];
                map[pin.LogicalPin.IDInFlow] = shortName + ".in";
                map[pin.LogicalPin.IDOutFlow] = shortName + ".out";
            }
        }
        return map;
    }

    private string FormatResultsTable(SweepResult result, Dictionary<Guid, string> pinNameMap)
    {
        if (result.DataPoints.Count == 0) return "No data points";

        // Only show pins that have non-zero power in at least one step
        var activePins = result.MonitoredPinIds
            .Where(pid => result.DataPoints.Any(dp =>
                dp.OutputPowers.TryGetValue(pid, out double p) && p > 1e-10))
            .Take(6) // Limit columns for readability
            .ToList();

        if (activePins.Count == 0) return "No signal detected";

        var sb = new StringBuilder();

        // Header
        sb.Append("Value".PadRight(8));
        foreach (var pid in activePins)
        {
            var name = pinNameMap.GetValueOrDefault(pid, pid.ToString()[..6]);
            if (name.Length > 12) name = name[..12];
            sb.Append(name.PadRight(14));
        }
        sb.AppendLine();

        // Separator
        sb.AppendLine(new string('-', 8 + activePins.Count * 14));

        // Data rows
        foreach (var dp in result.DataPoints)
        {
            sb.Append($"{dp.ParameterValue,7:F1} ");
            foreach (var pid in activePins)
            {
                dp.OutputPowers.TryGetValue(pid, out double power);
                sb.Append($"{power,13:F6} ");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
