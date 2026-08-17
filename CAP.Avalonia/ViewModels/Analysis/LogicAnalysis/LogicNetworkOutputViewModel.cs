using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;

/// <summary>
/// One network-level output tap of the assembled logic network, shown as a live
/// 0/1 indicator in the Logic panel.
/// </summary>
public partial class LogicNetworkOutputViewModel : ObservableObject
{
    /// <summary>Initializes the indicator for the output tap named <paramref name="pinName"/>.</summary>
    public LogicNetworkOutputViewModel(string pinName)
    {
        PinName = pinName;
    }

    /// <summary>Output tap name in <c>&lt;gate&gt;.&lt;pin&gt;</c> form.</summary>
    public string PinName { get; }

    /// <summary>The currently evaluated bit at this tap.</summary>
    [ObservableProperty]
    private bool _isOne;

    /// <summary>The bit as display text ("1" or "0").</summary>
    public string BitText => IsOne ? "1" : "0";

    partial void OnIsOneChanged(bool value) => OnPropertyChanged(nameof(BitText));
}
