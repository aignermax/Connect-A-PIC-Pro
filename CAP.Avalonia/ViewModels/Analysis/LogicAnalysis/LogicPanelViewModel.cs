using System.Collections.ObjectModel;
using System.ComponentModel;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Analysis.LogicAnalysis;
using CAP_Core.Components.ComponentHelpers;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;

/// <summary>
/// ViewModel behind the Logic panel (rung 4→5 of the NAND game): assembles the logic
/// network of the loaded design via <see cref="LogicNetworkAssembler"/> at the active
/// laser wavelength, shows every unconnected gate input as a toggle and every gate
/// output as a live 0/1 indicator. Toggling an input re-evaluates immediately —
/// <see cref="LogicNetworkEvaluator.Evaluate"/> is a pure truth-table lookup, so no
/// new simulation runs. Assembly failures (no gate in the design, invalid wiring) are
/// shown as readable status text, never as a crash.
/// </summary>
public partial class LogicPanelViewModel : ObservableObject
{
    private readonly LogicNetworkAssembler _assembler = new();
    private DesignCanvasViewModel? _canvas;
    private CancellationTokenSource? _buildCts;
    private LogicNetworkEvaluator? _network;

    /// <summary>True while the network is being assembled (spinner + Cancel button).</summary>
    [ObservableProperty]
    private bool _isProcessing;

    /// <summary>True when an assembled network is available for display.</summary>
    [ObservableProperty]
    private bool _hasNetwork;

    /// <summary>Status, hint, or validation message shown under the Build button.</summary>
    [ObservableProperty]
    private string _statusText = "";

    /// <summary>Display text for the wavelength the network was assembled at.</summary>
    [ObservableProperty]
    private string _wavelengthText = "";

    /// <summary>Network-level inputs (unconnected gate inputs), shown as toggles.</summary>
    public ObservableCollection<LogicNetworkInputViewModel> Inputs { get; } = new();

    /// <summary>Network-level output taps (every gate output), shown as 0/1 indicators.</summary>
    public ObservableCollection<LogicNetworkOutputViewModel> Outputs { get; } = new();

    /// <summary>Hands the panel the design canvas; called once from the RightPanel host.</summary>
    public void Configure(DesignCanvasViewModel canvas) => _canvas = canvas;

    private void ShowNetwork(LogicNetworkEvaluator network)
    {
        ClearNetwork();
        _network = network;
        foreach (var name in network.InputPinNames)
        {
            var input = new LogicNetworkInputViewModel(name);
            input.PropertyChanged += OnInputPropertyChanged;
            Inputs.Add(input);
        }
        foreach (var name in network.OutputPinNames)
        {
            Outputs.Add(new LogicNetworkOutputViewModel(name));
        }

        HasNetwork = true;
        ReEvaluate();
    }

    /// <summary>Replaces the displayed network with nothing (failure, cancel, rebuild).</summary>
    private void ClearNetwork()
    {
        _network = null;
        HasNetwork = false;
        foreach (var input in Inputs)
            input.PropertyChanged -= OnInputPropertyChanged;
        Inputs.Clear();
        Outputs.Clear();
    }

    /// <summary>A toggled input re-evaluates the whole network synchronously.</summary>
    private void OnInputPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogicNetworkInputViewModel.IsOn))
            ReEvaluate();
    }

    private void ReEvaluate()
    {
        if (_network == null)
            return;
        var bits = Inputs.ToDictionary(input => input.PinName, input => input.IsOn);
        var result = _network.Evaluate(bits);
        foreach (var output in Outputs)
            output.IsOne = result[output.PinName];
    }

    /// <summary>The active laser's wavelength, falling back to the standard red wavelength.</summary>
    private int ResolveWavelengthNm() =>
        _canvas?.Components.FirstOrDefault(c => c.IsLightSource)?.LaserConfig?.WavelengthNm
        ?? StandardWaveLengths.RedNM;

    private static string Translate(string key) => LocalizationService.Instance.Translate(key);
}
