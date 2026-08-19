using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;

/// <summary>
/// One external group pin offered as a checkbox in the Truth Table panel's
/// input, output, or bias list.
/// </summary>
public partial class PinSelectionViewModel : ObservableObject
{
    /// <summary>Initializes the checkbox entry for the pin named <paramref name="pinName"/>.</summary>
    public PinSelectionViewModel(string pinName)
    {
        PinName = pinName;
    }

    /// <summary>Name of the external group pin this checkbox assigns.</summary>
    public string PinName { get; }

    /// <summary>True when the pin is assigned to the corresponding role (input/output/bias).</summary>
    [ObservableProperty]
    private bool _isChecked;

    /// <summary>
    /// Signal name offered on input and output rows: input pins sharing a signal
    /// name become one network input in the Logic panel, and a named output pin's
    /// tap reads the signal name instead of the raw <c>&lt;gate&gt;.&lt;pin&gt;</c>
    /// id. Empty means "no signal" — the pin keeps its raw naming. Only the input
    /// and output lists bind this; bias rows never show the field.
    /// </summary>
    [ObservableProperty]
    private string _signalName = "";

    /// <summary>
    /// True when the row offers the signal-name field: the panel shows it once the
    /// group carries a persisted pin assignment (after extraction or load). The view
    /// additionally ANDs this with <see cref="IsChecked"/> — only checked input and
    /// output rows are editable.
    /// </summary>
    [ObservableProperty]
    private bool _signalEditingVisible;
}
