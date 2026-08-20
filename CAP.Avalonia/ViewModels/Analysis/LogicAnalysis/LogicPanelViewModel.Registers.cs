using System.Collections.ObjectModel;
using CAP_Core.Analysis.LogicAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;

/// <summary>
/// Register half of <see cref="LogicPanelViewModel"/> (issue #1099, rung 5): when
/// the assembled network contains at least one designated register, the panel
/// shows a Step-clock button that advances every register by exactly one clock
/// (<see cref="LogicNetworkEvaluator.Step"/>) and a compact readout of the
/// committed register outputs, so the held state stays visible even when the
/// canvas badges are off-screen. A purely combinational network has no registers —
/// the button is hidden and disabled then.
/// </summary>
public partial class LogicPanelViewModel
{
    /// <summary>True when the assembled network contains at least one register — only then is clocking meaningful.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StepClockCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleRunCommand))]
    private bool _hasRegisters;

    /// <summary>One row per register gate: its committed output bits, refreshed by every clock step.</summary>
    public ObservableCollection<LogicRegisterStateViewModel> RegisterStates { get; } = new();

    /// <summary>
    /// Advances every register by one clock: each register samples its inputs from
    /// the settled network and commits them, then the panel re-settles and refreshes
    /// the gate-output list and the canvas badges from the new state. The toggle
    /// timeline is discarded along the way — it described the pre-step settling and
    /// no longer matches the visible state (timeline integration of step events is
    /// a separate slice).
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasRegisters))]
    private void StepClock() => ClockOnce();

    /// <summary>
    /// One clock edge, shared by the Step button and every Run-mode tick (issue
    /// #1111): an auto-clock tick is defined to behave exactly like a Step press.
    /// </summary>
    private void ClockOnce()
    {
        if (_network == null)
            return;
        // Step samples the settled state of the last Evaluate call — but the
        // timeline's replay artifact re-evaluates the before-toggle bits on every
        // input change, so the last captured state can describe stale inputs.
        // Re-settle with the visible inputs first: the step must sample what the
        // user actually sees.
        var bits = Inputs.ToDictionary(input => input.PinName, input => input.IsOn);
        _network.Evaluate(bits);
        _network.Step();
        ReEvaluate();
        RefreshRegisterStates();
    }

    /// <summary>Builds the readout rows from the network's committed register state, one row per register gate.</summary>
    private void ShowRegisterStates(LogicNetworkEvaluator network)
    {
        HasRegisters = network.RegisterState.Count > 0;
        foreach (var gatePins in network.RegisterState.Keys
                     .GroupBy(pin => pin.GateId)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var row = new LogicRegisterStateViewModel(
                gatePins.Key,
                gatePins.Select(pin => pin.PinName).OrderBy(name => name, StringComparer.Ordinal).ToList());
            row.Refresh(network.RegisterState);
            RegisterStates.Add(row);
        }
    }

    /// <summary>Re-reads every register row's committed bits after a clock step.</summary>
    private void RefreshRegisterStates()
    {
        if (_network == null)
            return;
        foreach (var row in RegisterStates)
            row.Refresh(_network.RegisterState);
    }

    /// <summary>Drops the readout rows, halts the auto-clock, and hides the clock buttons together with the network behind them.</summary>
    private void ClearRegisterStates()
    {
        StopRun();
        RegisterStates.Clear();
        HasRegisters = false;
    }
}
