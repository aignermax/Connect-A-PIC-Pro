using CAP.Avalonia.Services.Localization;
using CAP_Core.Analysis.LogicAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;

/// <summary>
/// One optical fan-out warning shown in the Logic panel status area: which pin
/// (or input signal) fans out, to how many gate inputs, and the one-line physics
/// (a splitter per branch costs ~3 dB; real designs restore levels). Purely a
/// display wrapper over <see cref="LogicFanOutWarning"/> — the detection lives in
/// the logic layer.
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
            LocalizationService.Instance.Translate(lineKey),
            warning.DriverDisplayName,
            warning.LoadCount);
    }

    /// <summary>The detected fan-out warning behind this display entry.</summary>
    public LogicFanOutWarning Warning { get; }

    /// <summary>The formatted one-line warning (driver, load count) for the status area.</summary>
    public string WarningText { get; }
}
