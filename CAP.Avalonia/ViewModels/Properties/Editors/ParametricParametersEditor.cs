using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Core;
using CAP_Core.Components.Parametric;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.Properties.Editors;

/// <summary>
/// One editable physical parameter row (e.g. "Insertion Loss [dB]") in the
/// properties panel. Writes go straight into the bound <see cref="Slider"/>,
/// which updates the instance's S-matrix and re-triggers the simulation.
/// </summary>
public partial class ParameterRowViewModel : ObservableObject
{
    private readonly Slider _slider;
    private readonly Action? _onValueChanged;

    /// <summary>Tolerance for de-duping writes; matches the slider editor.</summary>
    private const double ValueEpsilon = 0.001;

    /// <summary>Display label of the parameter (from the PDK definition).</summary>
    public string Label { get; }

    /// <summary>Physical unit (e.g. "dB", "%"); empty when dimensionless.</summary>
    public string Unit { get; }

    /// <summary>True when a unit is defined, so the view can hide empty brackets.</summary>
    public bool HasUnit => Unit.Length > 0;

    /// <summary>Minimum allowed value.</summary>
    public double Min { get; }

    /// <summary>Maximum allowed value.</summary>
    public double Max { get; }

    /// <summary>Current value; writes clamp to range and update the simulation.</summary>
    public double Value
    {
        get => _slider.Value;
        set
        {
            double clamped = Math.Clamp(value, Min, Max);
            if (Math.Abs(_slider.Value - clamped) < ValueEpsilon) return;
            _slider.Value = clamped;
            OnPropertyChanged(nameof(Value));
            _onValueChanged?.Invoke();
        }
    }

    /// <summary>Creates a row for one parameter bound to a component slider.</summary>
    public ParameterRowViewModel(ParameterDefinition definition, Slider slider, Action? onValueChanged)
    {
        _slider = slider;
        _onValueChanged = onValueChanged;
        Label = definition.Label;
        Unit = definition.Unit;
        Min = definition.MinValue;
        Max = definition.MaxValue;
    }
}

/// <summary>
/// Editor ViewModel for parametric components: lists every named
/// physical parameter (MMI insertion loss / splitting ratio, coupler coupling
/// ratio, …) with its label, unit and range, editable per placed instance.
/// </summary>
public partial class ParametricParametersEditorViewModel : ObservableObject
{
    /// <summary>Display name of the underlying component.</summary>
    public string ComponentName { get; }

    /// <summary>One editable row per slider-bound parameter.</summary>
    public IReadOnlyList<ParameterRowViewModel> Rows { get; }

    /// <summary>Builds the rows from the component's parameter metadata.</summary>
    public ParametricParametersEditorViewModel(ComponentViewModel componentVm)
    {
        ComponentName = componentVm.DisplayName;
        Rows = BuildRows(componentVm);
    }

    private static IReadOnlyList<ParameterRowViewModel> BuildRows(ComponentViewModel componentVm)
    {
        var rows = new List<ParameterRowViewModel>();
        foreach (var definition in componentVm.Component.ParameterDefinitions)
        {
            if (definition.SliderNumber is not int sliderNumber) continue;
            var slider = componentVm.Component.GetSlider(sliderNumber);
            if (slider == null) continue;
            // Late-bound: OnSliderChanged is wired by the canvas after the VM is
            // created, so resolve it at invoke time rather than capturing the value.
            rows.Add(new ParameterRowViewModel(definition, slider,
                () => componentVm.OnSliderChanged?.Invoke()));
        }
        return rows;
    }
}

/// <summary>
/// Provider that surfaces the parameter editor for components carrying named
/// parameter metadata. Registered before the generic slider editor so
/// parametric components get labeled, unit-aware rows instead of one
/// anonymous slider.
/// </summary>
public class ParametricParametersEditorProvider : IComponentEditorProvider
{
    /// <inheritdoc/>
    public object? TryCreateEditor(ComponentViewModel componentVm)
    {
        if (componentVm.IsLightSource) return null;
        if (componentVm.Component.IsAnalysisTool) return null;

        var editor = new ParametricParametersEditorViewModel(componentVm);
        return editor.Rows.Count > 0 ? editor : null;
    }
}
