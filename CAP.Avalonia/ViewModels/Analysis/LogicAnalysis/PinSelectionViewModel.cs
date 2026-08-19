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
    /// Network-signal name offered on input rows (issue #1033): input pins sharing a
    /// signal name become one network input in the Logic panel. Empty means "no
    /// signal" — the pin keeps its <c>&lt;gate&gt;.&lt;pin&gt;</c> naming. Only the
    /// input list binds this; output and bias rows never show the field.
    /// </summary>
    [ObservableProperty]
    private string _signalName = "";

    /// <summary>
    /// True when the row offers the signal-name field: the panel shows it once the
    /// group carries a persisted pin assignment (after extraction or load). The view
    /// additionally ANDs this with <see cref="IsChecked"/> — only checked input rows
    /// are editable.
    /// </summary>
    [ObservableProperty]
    private bool _signalEditingVisible;
}
