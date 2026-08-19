using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Analysis.LogicAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;

/// <summary>
/// Replay half of <see cref="LogicPanelViewModel"/> (issue #1058, rung 5 visualizer
/// slice 3): selecting a timeline event — by clicking its row or stepping with the
/// Prev/Next buttons — freezes the canvas gate-state badges at that instant. The state
/// at time t_k is a pure lookup over the already-computed timeline and the before/after
/// input assignments: every gate output pin whose switch event has time ≤ t_k shows its
/// new value, every other pin still shows its before value. No physics is recomputed.
/// Deselecting (clicking the selected row again, the "back to live" button, or a new
/// input toggle) returns the badges to the live end state.
/// </summary>
public partial class LogicPanelViewModel
{
    /// <summary>Gate output bits before the toggle that produced the current timeline.</summary>
    private IReadOnlyDictionary<string, bool>? _replayBeforeResult;

    /// <summary>The live end state of the current evaluation — the badges' resting state.</summary>
    private IReadOnlyDictionary<string, bool>? _liveResult;

    /// <summary>
    /// The input bits of the current evaluation. Replay reuses them for the named input
    /// chips: the toggle enters at t = 0, so at every replayed instant the inputs
    /// already carry their new values.
    /// </summary>
    private IReadOnlyDictionary<string, bool>? _liveInputBits;

    /// <summary>
    /// The timeline event the canvas currently replays, or null when the badges show the
    /// live end state. Setting it pushes the state at that event's time onto the canvas.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReplayActive))]
    [NotifyCanExecuteChangedFor(nameof(PreviousTimelineEventCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextTimelineEventCommand))]
    private LogicTimelineEventViewModel? _selectedTimelineEvent;

    /// <summary>True while the canvas replays an intermediate state instead of the live one.</summary>
    public bool IsReplayActive => SelectedTimelineEvent != null;

    /// <summary>The "showing t = X ps" line displayed while replay is active.</summary>
    [ObservableProperty]
    private string _replayTimeText = "";

    /// <summary>The selected event's position in the timeline, or -1 when nothing is selected.</summary>
    private int SelectedIndex =>
        SelectedTimelineEvent == null ? -1 : TimelineEvents.IndexOf(SelectedTimelineEvent);

    /// <summary>
    /// Selects a timeline row for replay; clicking the already-selected row deselects it
    /// and returns the canvas to the live end state. A manual (de)selection stops the
    /// auto-play.
    /// </summary>
    [RelayCommand]
    private void SelectTimelineEvent(LogicTimelineEventViewModel? row)
    {
        if (row == null)
            return;
        StopPlayback();
        SelectedTimelineEvent = ReferenceEquals(SelectedTimelineEvent, row) ? null : row;
    }

    /// <summary>Steps the replay one event earlier; manual stepping stops the auto-play.</summary>
    [RelayCommand(CanExecute = nameof(CanStepToPreviousEvent))]
    private void PreviousTimelineEvent()
    {
        StopPlayback();
        StepTo(SelectedIndex - 1);
    }

    /// <summary>Steps the replay one event later; from the live state this selects the first event.</summary>
    [RelayCommand(CanExecute = nameof(CanStepToNextEvent))]
    private void NextTimelineEvent()
    {
        StopPlayback();
        StepTo(SelectedTimelineEvent == null ? 0 : SelectedIndex + 1);
    }

    /// <summary>Leaves replay mode — the badges return to the live end state.</summary>
    [RelayCommand]
    private void ExitReplay()
    {
        StopPlayback();
        SelectedTimelineEvent = null;
    }

    private bool CanStepToPreviousEvent() => SelectedIndex > 0;

    private bool CanStepToNextEvent() =>
        TimelineEvents.Count > 0
        && (SelectedTimelineEvent == null || SelectedIndex < TimelineEvents.Count - 1);

    private void StepTo(int index)
    {
        if (index < 0 || index >= TimelineEvents.Count)
            return;
        SelectedTimelineEvent = TimelineEvents[index];
    }

    /// <summary>
    /// Pushes the replay state onto the canvas: the before-toggle bits with every switch
    /// event up to the selected time applied. Deselection restores the live end state.
    /// </summary>
    partial void OnSelectedTimelineEventChanged(LogicTimelineEventViewModel? value)
    {
        foreach (var row in TimelineEvents)
            row.IsSelected = ReferenceEquals(row, value);
        ReplayTimeText = value == null
            ? ""
            : string.Format(Translate("LogicPanel.ReplayTime"), value.Event.TimePicoseconds);
        if (value == null)
        {
            RestoreLiveBadges();
            return;
        }
        ApplyReplayState(value.Event.TimePicoseconds);
    }

    /// <summary>
    /// The state at time t: before bits plus every switch event with time ≤ t.
    /// Evaluation results key by tap name (a signal-named output reads as <c>S0</c>,
    /// not <c>gate.pin</c>), so each event's pin is mapped back to its tap first.
    /// </summary>
    private void ApplyReplayState(double timePicoseconds)
    {
        if (_canvas == null || _network == null || _replayBeforeResult == null || _liveInputBits == null)
            return;
        var tapNamesByPin = _network.OutputTaps.ToDictionary(tap => tap.Value, tap => tap.Key);
        var state = new Dictionary<string, bool>(_replayBeforeResult);
        foreach (var row in TimelineEvents)
        {
            if (row.Event.TimePicoseconds > timePicoseconds)
                break;
            state[tapNamesByPin[new LogicPinRef(row.Event.GateId, row.Event.OutputPin)]] = row.Event.NewValue;
        }
        _canvas.LogicGateStates.ShowStates(BadgeStatesOf(state, _liveInputBits));
    }

    /// <summary>Returns the canvas badges to the live end state after replay ends.</summary>
    private void RestoreLiveBadges()
    {
        if (_canvas == null || _liveResult == null || _liveInputBits == null)
            return;
        _canvas.LogicGateStates.ShowStates(BadgeStatesOf(_liveResult, _liveInputBits));
    }

    /// <summary>
    /// The badge states of one evaluation result: the anonymous chip per gate output
    /// pin (walked through the tap names the result keys by) plus the named input
    /// chips of the given input assignment (issue #1051).
    /// </summary>
    private IEnumerable<LogicGateBadgeState> BadgeStatesOf(
        IReadOnlyDictionary<string, bool> result, IReadOnlyDictionary<string, bool> inputBits)
    {
        if (_network == null)
            yield break;
        foreach (var tap in _network.OutputTaps)
            yield return new LogicGateBadgeState(tap.Value.GateId, tap.Value.PinName, result[tap.Key]);
        var signalNamesByGate = PersistedInputSignalNamesByGate();
        foreach (var gateId in _network.Gates.Keys)
        {
            foreach (var badge in NamedInputBadges(gateId, signalNamesByGate, inputBits))
                yield return badge;
        }
    }

    /// <summary>Discards every replay artifact together with the timeline behind it.</summary>
    private void ClearReplay()
    {
        // The live/before results go first: clearing the selection re-pushes the live
        // badges, which must no-op once the network behind them is gone.
        StopPlayback();
        _liveResult = null;
        _liveInputBits = null;
        _replayBeforeResult = null;
        SelectedTimelineEvent = null;
    }
}
