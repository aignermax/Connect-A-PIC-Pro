using System.Collections.ObjectModel;
using System.ComponentModel;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Analysis.LogicAnalysis;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.Components.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;

/// <summary>
/// ViewModel behind the Truth Table panel: when exactly one <see cref="ComponentGroup"/>
/// is selected on the canvas, its external pins can be assigned as logic inputs (at most
/// <see cref="TruthTableExtractor.MaxLogicInputs"/>), outputs, or bias pins (constantly
/// "on" in every row — the ingredient inversion gates need), and the panel extracts
/// the group's truth table via <see cref="TruthTableExtractor"/> — every output bit shown
/// together with the raw simulated power behind it. A pin is exactly one of input, output,
/// or bias: checking it in one list revokes it in the other two.
/// </summary>
public partial class TruthTableViewModel : ObservableObject
{
    private const double DefaultThreshold = 0.5;

    private readonly TruthTableExtractor _extractor = new();
    private ComponentGroup? _group;
    private DesignCanvasViewModel? _canvas;
    private CancellationTokenSource? _extractCts;
    private bool _revertingPinCheck;

    /// <summary>True while exactly one group is selected and the panel is active.</summary>
    [ObservableProperty]
    private bool _isGroupSelected;

    /// <summary>True while an extraction runs (spinner + Cancel button).</summary>
    [ObservableProperty]
    private bool _isProcessing;

    /// <summary>Normalized power threshold in the open interval (0, 1); an output is logic 1 at or above it.</summary>
    [ObservableProperty]
    private double _threshold = DefaultThreshold;

    /// <summary>Status, hint, or validation message shown under the Extract button.</summary>
    [ObservableProperty]
    private string _statusText = "";

    /// <summary>True when a truth table result is available for display.</summary>
    [ObservableProperty]
    private bool _hasResult;

    /// <summary>Bias-pin assignment of the extracted table, shown above the result (empty when none).</summary>
    [ObservableProperty]
    private string _biasSummaryText = "";

    /// <summary>Display text for the wavelength the table will be extracted at.</summary>
    [ObservableProperty]
    private string _wavelengthText = "";

    /// <summary>External pins of the selected group offered as logic inputs (checkboxes).</summary>
    public ObservableCollection<PinSelectionViewModel> InputPins { get; } = new();

    /// <summary>External pins of the selected group offered as logic outputs (checkboxes).</summary>
    public ObservableCollection<PinSelectionViewModel> OutputPins { get; } = new();

    /// <summary>External pins of the selected group offered as bias pins (checkboxes) — constantly "on" in every row.</summary>
    public ObservableCollection<PinSelectionViewModel> BiasPins { get; } = new();

    /// <summary>Input pin names of the extracted table, in bit order (table header).</summary>
    public ObservableCollection<string> InputHeaders { get; } = new();

    /// <summary>Output pin names of the extracted table (table header).</summary>
    public ObservableCollection<string> OutputHeaders { get; } = new();

    /// <summary>The extracted truth table rows, in binary counting order.</summary>
    public ObservableCollection<TruthTableRowViewModel> Rows { get; } = new();

    /// <summary>
    /// Activates the panel for the current selection: exactly one selected
    /// <see cref="ComponentGroup"/> exposes its external pins; anything else shows a hint.
    /// Called from the selection-changed callback in MainViewModel.
    /// </summary>
    public void ConfigureForSelection(ComponentViewModel? component, DesignCanvasViewModel? canvas)
    {
        // A late-finishing extraction must not display results for the newly
        // selected/deselected group.
        _extractCts?.Cancel();
        _canvas = canvas;
        _group = component?.Component as ComponentGroup;
        var singleSelection = canvas == null || canvas.Selection.SelectedComponents.Count <= 1;
        IsGroupSelected = _group != null && singleSelection;

        HasResult = false;
        Rows.Clear();
        StatusText = "";
        BiasSummaryText = "";
        RebuildPinLists();
        WavelengthText = IsGroupSelected
            ? string.Format(Translate("TruthTable.Wavelength"), ResolveWavelengthNm())
            : "";
    }

    private void RebuildPinLists()
    {
        DetachPinHandlers();
        InputPins.Clear();
        OutputPins.Clear();
        BiasPins.Clear();
        if (!IsGroupSelected || _group == null)
            return;

        foreach (var pin in _group.ExternalPins)
        {
            InputPins.Add(CreatePin(pin.Name));
            OutputPins.Add(CreatePin(pin.Name));
            BiasPins.Add(CreatePin(pin.Name));
        }
    }

    /// <summary>Builds one checkbox entry and wires the pin-role invariant handler.</summary>
    private PinSelectionViewModel CreatePin(string pinName)
    {
        var pin = new PinSelectionViewModel(pinName);
        pin.PropertyChanged += OnPinPropertyChanged;
        return pin;
    }

    private void DetachPinHandlers()
    {
        foreach (var pin in InputPins.Concat(OutputPins).Concat(BiasPins))
            pin.PropertyChanged -= OnPinPropertyChanged;
    }

    /// <summary>
    /// Enforces the pin-role invariants directly at the checkbox: a pin is at most one
    /// of input, output, or bias (checking it in one list revokes it in the other two),
    /// and at most <see cref="TruthTableExtractor.MaxLogicInputs"/> inputs may be checked.
    /// </summary>
    private void OnPinPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_revertingPinCheck || e.PropertyName != nameof(PinSelectionViewModel.IsChecked))
            return;
        if (sender is not PinSelectionViewModel pin || !pin.IsChecked)
            return;

        _revertingPinCheck = true;
        try
        {
            UncheckSamePinInOtherLists(pin);
            EnforceInputLimit(pin);
        }
        finally
        {
            _revertingPinCheck = false;
        }
    }

    /// <summary>Unchecks the same pin name in the two lists the just-checked pin does not belong to.</summary>
    private void UncheckSamePinInOtherLists(PinSelectionViewModel checkedPin)
    {
        foreach (var list in new[] { InputPins, OutputPins, BiasPins })
        {
            if (list.Contains(checkedPin))
                continue;
            var twin = list.FirstOrDefault(p => p.PinName == checkedPin.PinName);
            if (twin != null)
                twin.IsChecked = false;
        }
    }

    /// <summary>Reverts a fresh input check that would exceed the extractor's input limit.</summary>
    private void EnforceInputLimit(PinSelectionViewModel checkedPin)
    {
        if (!InputPins.Contains(checkedPin))
            return;
        if (InputPins.Count(p => p.IsChecked) <= TruthTableExtractor.MaxLogicInputs)
            return;

        checkedPin.IsChecked = false;
        StatusText = string.Format(Translate("Analysis.TruthTable.TooManyInputs"), TruthTableExtractor.MaxLogicInputs);
    }

    /// <summary>The active laser's wavelength, falling back to the standard red wavelength.</summary>
    private int ResolveWavelengthNm() =>
        _canvas?.Components.FirstOrDefault(c => c.IsLightSource)?.LaserConfig?.WavelengthNm
        ?? StandardWaveLengths.RedNM;

    private static string Translate(string key) => LocalizationService.Instance.Translate(key);
}
