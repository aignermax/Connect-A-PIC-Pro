using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis.BusView;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;

/// <summary>
/// One network-level output tap of the assembled logic network, shown as a live
/// 0/1 indicator in the Logic panel. A tap named by an output signal shows that
/// name; the raw tapped pin rides along for the tooltip. Doubles as a plain
/// (non-grouped) Outputs row of the bus view (issue #1068).
/// </summary>
public partial class LogicNetworkOutputViewModel : LogicOutputRowViewModel
{
    /// <summary>Initializes the indicator for the output tap named <paramref name="pinName"/>.</summary>
    public LogicNetworkOutputViewModel(string pinName)
    {
        PinName = pinName;
    }

    /// <summary>
    /// Output tap name: the output signal name when the tapped pin carries one,
    /// else the raw <c>&lt;gate&gt;.&lt;pin&gt;</c> id.
    /// </summary>
    public string PinName { get; }

    /// <summary>The tapped gate output pin in raw <c>&lt;gate&gt;.&lt;pin&gt;</c> form (tooltip).</summary>
    [ObservableProperty]
    private string _rawPinName = "";

    /// <summary>The currently evaluated bit at this tap.</summary>
    [ObservableProperty]
    private bool _isOne;

    /// <summary>The tapped gate's propagation delay as display text (e.g. "12.3 ps").</summary>
    [ObservableProperty]
    private string _delayText = "";

    /// <summary>The bit as display text ("1" or "0").</summary>
    public string BitText => IsOne ? "1" : "0";

    partial void OnIsOneChanged(bool value) => OnPropertyChanged(nameof(BitText));
}
