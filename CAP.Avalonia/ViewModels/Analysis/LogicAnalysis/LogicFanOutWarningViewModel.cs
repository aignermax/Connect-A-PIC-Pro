using CAP.Avalonia.Services.Localization;
using CAP_Core.Analysis.LogicAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;

/// <summary>
/// One optical fan-out warning shown in the Logic panel status area: which pin
/// (or input signal) fans out, to how many gate inputs, and the one-line physics
/// (a splitter per branch costs ~3 dB; real designs restore levels). Below the
/// warning line, the quantitative level report (#1011) shows the ideal-split
/// arithmetic — driver power, per-branch power, splitting loss in dB — and one
/// verdict per receiving input: would the branch power still reach that gate's
/// threshold. Purely a display wrapper over <see cref="LogicFanOutWarning"/> —
/// the detection and the level math live in the logic layer.
/// </summary>
public partial class LogicFanOutWarningViewModel : ObservableObject
{
    /// <summary>Wraps one detected fan-out warning for display.</summary>
    public LogicFanOutWarningViewModel(LogicFanOutWarning warning)
    {
        Warning = warning;
        var lineKey = warning.IsNetworkInputSignal
            ? "LogicPanel.FanOutWarning.InputLine"
            : "LogicPanel.FanOutWarning.GateLine";
        WarningText = string.Format(
            Translate(lineKey),
            warning.DriverDisplayName,
            warning.LoadCount);
        SplitLine = string.Format(
            Translate("LogicPanel.FanOutWarning.SplitLine"),
            warning.LoadCount,
            warning.Levels.DriverPowerOne,
            warning.Levels.BranchPower,
            warning.Levels.SplitLossDb);
        VerdictLines = warning.Levels.Branches.Select(branch => string.Format(
                Translate(branch.ReadsAsOne
                    ? "LogicPanel.FanOutWarning.BranchStillOne"
                    : "LogicPanel.FanOutWarning.BranchWouldFail"),
                branch.LoadName,
                branch.Threshold))
            .ToList();
    }

    /// <summary>The detected fan-out warning behind this display entry.</summary>
    public LogicFanOutWarning Warning { get; }

    /// <summary>The formatted one-line warning (driver, load count) for the status area.</summary>
    public string WarningText { get; }

    /// <summary>The ideal-split arithmetic: 1×N split, driver power → per-branch power, loss in dB.</summary>
    public string SplitLine { get; }

    /// <summary>One verdict line per receiving input: would it still read a logic 1.</summary>
    public IReadOnlyList<string> VerdictLines { get; }

    private static string Translate(string key) => LocalizationService.Instance.Translate(key);
}
