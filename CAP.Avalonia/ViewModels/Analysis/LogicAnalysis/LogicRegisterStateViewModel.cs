using CAP_Core.Analysis.LogicAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;

/// <summary>
/// One register gate's row in the Logic panel's register-state readout (issue
/// #1099): the gate name next to its committed output bits (<c>y = 1</c>), so the
/// held state stays visible even when the canvas badges are off-screen. The row's
/// bit text refreshes after every clock step; the gate and pin names are fixed at
/// build time.
/// </summary>
public partial class LogicRegisterStateViewModel : ObservableObject
{
    private readonly IReadOnlyList<string> _outputPinNames;

    /// <summary>Initializes the row for the register gate <paramref name="gateName"/>.</summary>
    public LogicRegisterStateViewModel(string gateName, IReadOnlyList<string> outputPinNames)
    {
        GateName = gateName;
        _outputPinNames = outputPinNames;
    }

    /// <summary>The register gate's name — the network's gate id.</summary>
    public string GateName { get; }

    /// <summary>The committed output bits as display text (<c>y = 1</c>, comma-separated per pin).</summary>
    [ObservableProperty]
    private string _bitsText = "";

    /// <summary>Re-reads this register's committed outputs after a clock step.</summary>
    public void Refresh(IReadOnlyDictionary<LogicPinRef, bool> committedState) =>
        BitsText = string.Join(", ", _outputPinNames.Select(pinName =>
            $"{pinName} = {(committedState[new LogicPinRef(GateName, pinName)] ? "1" : "0")}"));
}
