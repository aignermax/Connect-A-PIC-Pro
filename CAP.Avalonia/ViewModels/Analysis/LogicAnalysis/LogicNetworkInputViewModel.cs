using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis.BusView;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;

/// <summary>
/// One network-level input of the assembled logic network, shown as a toggle in the
/// Logic panel. Toggling re-evaluates the network immediately (pure table lookup).
/// Doubles as a plain (non-grouped) Inputs row of the bus view (issue #1068).
/// </summary>
public partial class LogicNetworkInputViewModel : LogicInputRowViewModel
{
    /// <summary>Initializes the toggle for the network input named <paramref name="pinName"/>.</summary>
    public LogicNetworkInputViewModel(string pinName)
    {
        PinName = pinName;
    }

    /// <summary>
    /// Network input name: the signal name (issue #1025) for pins that carry one —
    /// one toggle per signal, e.g. the full adder's <c>A</c>, <c>B</c>, <c>Cin</c> —
    /// or <c>&lt;gate&gt;.&lt;pin&gt;</c> for an unmerged pin.
    /// </summary>
    public string PinName { get; }

    /// <summary>The bit currently driven into the network (initially off).</summary>
    [ObservableProperty]
    private bool _isOn;
}
