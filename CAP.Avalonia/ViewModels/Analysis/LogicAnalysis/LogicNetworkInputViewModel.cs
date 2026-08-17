using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;

/// <summary>
/// One network-level input of the assembled logic network, shown as a toggle in the
/// Logic panel. Toggling re-evaluates the network immediately (pure table lookup).
/// </summary>
public partial class LogicNetworkInputViewModel : ObservableObject
{
    /// <summary>Initializes the toggle for the network input named <paramref name="pinName"/>.</summary>
    public LogicNetworkInputViewModel(string pinName)
    {
        PinName = pinName;
    }

    /// <summary>Network input name in <c>&lt;gate&gt;.&lt;pin&gt;</c> form.</summary>
    public string PinName { get; }

    /// <summary>The bit currently driven into the network (initially off).</summary>
    [ObservableProperty]
    private bool _isOn;
}
