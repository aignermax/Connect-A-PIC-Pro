using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;

/// <summary>
/// One external group pin offered as a checkbox in the Truth Table panel's
/// input or output list.
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

    /// <summary>True when the pin is assigned to the corresponding role (input/output).</summary>
    [ObservableProperty]
    private bool _isChecked;
}
