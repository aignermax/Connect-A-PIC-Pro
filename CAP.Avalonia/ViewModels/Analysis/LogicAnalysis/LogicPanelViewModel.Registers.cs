using System.Collections.ObjectModel;
using CAP_Core.Analysis.LogicAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;

/// <summary>
/// Register half of <see cref="LogicPanelViewModel"/> (issue #1099, rung 5): when
/// the assembled network contains at least one designated register, the panel
/// shows a Step-clock button that advances every register by exactly one clock
/// (<see cref="LogicNetworkEvaluator.Step"/>), a Reset button that returns every
/// register to its power-up state without rebuilding the network
/// (<see cref="LogicNetworkEvaluator.ResetRegisters"/>, issue #1127), and a
/// compact readout of the committed register outputs, so the held state stays
/// visible even when the canvas badges are off-screen. A purely combinational
/// network has no registers — the buttons are hidden and disabled then.
/// </summary>
public partial class LogicPanelViewModel
{
    /// <summary>True when the assembled network contains at least one register — only then is clocking meaningful.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StepClockCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetRegistersCommand))]
    private bool _hasRegisters;

    /// <summary>One row per register gate: its committed output bits, refreshed by every clock step.</summary>
    public ObservableCollection<LogicRegisterStateViewModel> RegisterStates { get; } = new();

    /// <summary>
    /// Advances every register by one clock: each register samples its inputs from
    /// the settled network and commits them, then the panel re-settles and refreshes
    /// the gate-output list and the canvas badges from the new state. The step's
    /// commit and ripple entries join the toggle timeline behind a "clock #k"
    /// divider (issue #1110), so the execution visualizer shows the clocked network
    /// advancing — inputs settled → clock → registers committed → outputs rippled.
    /// A step also exits replay: the badges show the new live state, and a frozen
    /// pre-step instant would otherwise stay highlighted while the state moved on.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasRegisters))]
    private void StepClock()
    {
        if (_network == null)
            return;
        // Step samples the settled state of the last Evaluate call — but the
        // timeline's replay artifact re-evaluates the before-toggle bits on every
        // input change, so the last captured state can describe stale inputs.
        // Re-settle with the visible inputs first: the step must sample what the
        // user actually sees. The pre-step result is also the replay before-state
        // of a timeline the step starts.
        var bits = Inputs.ToDictionary(input => input.PinName, input => input.IsOn);
        var preStepResult = _network.Evaluate(bits);
        var stepEvents = _network.Step();
        StopPlayback();
        SelectedTimelineEvent = null;
        ReEvaluate();
        AppendClockStepEvents(stepEvents, preStepResult);
        RefreshRegisterStates();
    }

    /// <summary>
    /// Returns every register to its power-up state — all committed outputs 0 —
    /// without rebuilding the network: the per-gate truth tables and delays stay
    /// valid (issue #1127). The panel re-settles with the visible input toggles
    /// (the same settle path a clock step uses), so the count snaps back and the
    /// outputs, canvas badges, and bus rows refresh from the cleared state. The
    /// timeline restarts at the reset's fresh settle phase — the clock counter
    /// returns to 0, so the next step is "clock #1" again — and any active replay
    /// or playback ends. A purely combinational network has no registers: the
    /// button stays hidden and disabled.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasRegisters))]
    private void ResetRegisters()
    {
        if (_network == null)
            return;
        // Like a clock step, the reset re-settles with the visible toggles first:
        // the timeline's replay artifact can leave the last captured state
        // describing stale inputs.
        var bits = Inputs.ToDictionary(input => input.PinName, input => input.IsOn);
        var preResetResult = _network.Evaluate(bits);
        var resetEvents = _network.ResetRegisters();
        StopPlayback();
        RestartTimelineAtFreshSettle(resetEvents, preResetResult);
        ReEvaluate();
        RefreshRegisterStates();
        // ReEvaluate refreshed the live bits without touching the timeline (the input
        // toggles did not move), so the waveform's live levels would be stale.
        RefreshWaveform();
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

    /// <summary>Drops the readout rows and the clock button together with the network behind them.</summary>
    private void ClearRegisterStates()
    {
        RegisterStates.Clear();
        HasRegisters = false;
    }
}
