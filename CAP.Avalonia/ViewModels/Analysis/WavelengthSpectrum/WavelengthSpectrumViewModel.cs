using CAP_Core.Analysis.OnaAnalysis;
using CAP_Core.Analysis.WavelengthSpectrum;
using CAP_Core.LightCalculation;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Canvas;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OxyPlot;

namespace CAP.Avalonia.ViewModels.Analysis.WavelengthSpectrum;

/// <summary>
/// ViewModel for the Spectrum tab (#816): sweeps the circuit across a wavelength
/// range and plots linear transmission |S|² per output pin — the standard
/// photonics spectrum view. Once a sweep has run, changing any sweep parameter
/// re-runs it automatically (debounced), so the plot stays live without a reload.
/// </summary>
public partial class WavelengthSpectrumViewModel : ObservableObject
{
    /// <summary>Debounce applied before a parameter change triggers an automatic re-sweep.</summary>
    internal static readonly TimeSpan DefaultAutoRefreshDelay = TimeSpan.FromMilliseconds(600);

    [ObservableProperty] private int _startNm = 1500;
    [ObservableProperty] private int _endNm = 1600;
    [ObservableProperty] private int _stepCount = 100;
    [ObservableProperty] private bool _isSweeping;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private PlotModel _plotModel = WavelengthSpectrumPlotBuilder.CreateEmptyPlotModel();

    /// <summary>True once a completed sweep is available (enables the plot and auto-refresh).</summary>
    [ObservableProperty] private bool _hasResult;

    /// <summary>Debounce used by the auto-refresh; tests set this to zero.</summary>
    internal TimeSpan AutoRefreshDelay { get; set; } = DefaultAutoRefreshDelay;

    /// <summary>The pending debounced auto-refresh, awaited by tests; null when idle.</summary>
    internal Task? PendingAutoRefresh { get; private set; }

    private readonly CAP_Core.ErrorConsoleService? _errorConsole;
    private readonly SemaphoreSlim _sweepGate = new(1, 1);
    private DesignCanvasViewModel? _canvas;
    private CancellationTokenSource? _sweepCts;
    private CancellationTokenSource? _debounceCts;

    /// <summary>Initializes a new instance of <see cref="WavelengthSpectrumViewModel"/>.</summary>
    /// <param name="errorConsole">Optional service for error logging.</param>
    public WavelengthSpectrumViewModel(CAP_Core.ErrorConsoleService? errorConsole = null)
    {
        _errorConsole = errorConsole;
    }

    /// <summary>Configures the panel with the current canvas context.</summary>
    /// <param name="canvas">Canvas providing components and connections.</param>
    public void Configure(DesignCanvasViewModel? canvas)
    {
        _canvas = canvas;
        StatusText = "";
        HasResult = false;
        PlotModel = WavelengthSpectrumPlotBuilder.CreateEmptyPlotModel();
    }

    /// <summary>Runs the wavelength sweep and updates the transmission plot.</summary>
    [RelayCommand]
    private Task RunSweep() => RunSweepInternalAsync();

    /// <summary>Cancels a running sweep.</summary>
    [RelayCommand]
    private void CancelSweep() => _sweepCts?.Cancel();

    partial void OnStartNmChanged(int value) => ScheduleAutoRefresh();
    partial void OnEndNmChanged(int value) => ScheduleAutoRefresh();
    partial void OnStepCountChanged(int value) => ScheduleAutoRefresh();

    /// <summary>
    /// Re-runs the sweep automatically after a parameter change — but only once
    /// the user has run a first sweep, so typing values before the first run
    /// doesn't kick off surprise simulations.
    /// </summary>
    private void ScheduleAutoRefresh()
    {
        if (!HasResult) return;
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        PendingAutoRefresh = AutoRefreshAsync(_debounceCts.Token);
    }

    private async Task AutoRefreshAsync(CancellationToken token)
    {
        try { await Task.Delay(AutoRefreshDelay, token); }
        catch (OperationCanceledException) { return; }
        if (token.IsCancellationRequested) return;
        await RunSweepInternalAsync();
    }

    private async Task RunSweepInternalAsync()
    {
        if (_canvas == null) return;

        // A newer request supersedes a running sweep: cancel it, then take the gate.
        _sweepCts?.Cancel();
        await _sweepGate.WaitAsync();
        try
        {
            IsSweeping = true;
            _sweepCts = new CancellationTokenSource();
            await ExecuteSweepAsync(_sweepCts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText = LocalizationService.Instance.Translate("Analysis.Spectrum.Cancelled");
        }
        catch (Exception ex)
        {
            _errorConsole?.LogError($"Spectrum sweep failed: {ex.Message}", ex);
            StatusText = string.Format(
                LocalizationService.Instance.Translate("Analysis.Common.Failed"), ex.Message);
        }
        finally
        {
            IsSweeping = false;
            _sweepCts?.Dispose();
            _sweepCts = null;
            _sweepGate.Release();
        }
    }

    private async Task ExecuteSweepAsync(CancellationToken token)
    {
        if (!TryCreateConfiguration(out var config)) return;

        var circuit = SpectrumSweepCircuitFactory.Create(_canvas!);
        if (circuit == null)
        {
            StatusText = LocalizationService.Instance.Translate("Analysis.Common.NoCircuit");
            return;
        }
        if (circuit.Ports.GetAllExternalInputs().Count == 0)
        {
            _errorConsole?.LogError(
                "Spectrum sweep aborted: no laser is switched on — turn the laser on at your input coupler.");
            StatusText = LocalizationService.Instance.Translate("Analysis.Spectrum.NoLight");
            return;
        }

        StatusText = string.Format(
            LocalizationService.Instance.Translate("Analysis.Spectrum.Running"), config!.StepCount);

        var sweeper = new WavelengthSweeper(new SystemMatrixBuilder(circuit.GridManager), circuit.Ports);
        var result = await sweeper.RunSweepAsync(config, circuit.GridManager, token);

        foreach (var warning in result.Warnings)
            _errorConsole?.LogWarning(warning);

        var curves = TransmissionSpectrumBuilder.Build(result, circuit.OutputCouplerPinIds);
        PlotModel = WavelengthSpectrumPlotBuilder.BuildPlotModel(
            curves,
            pinId => circuit.PinNames.TryGetValue(pinId, out var name) ? name : null,
            circuit.DesignWavelengthNm);
        HasResult = true;

        StatusText = curves.All(c => c.IsAtNoiseFloor)
            ? LocalizationService.Instance.Translate("Analysis.Spectrum.AllAtFloor")
            : string.Format(
                LocalizationService.Instance.Translate("Analysis.Spectrum.Complete"),
                result.DataPoints.Count);
    }

    private bool TryCreateConfiguration(out WavelengthSweepConfiguration? config)
    {
        try
        {
            config = new WavelengthSweepConfiguration(StartNm, EndNm, StepCount);
            return true;
        }
        catch (ArgumentException ex)
        {
            config = null;
            StatusText = ex.Message;
            return false;
        }
    }
}
