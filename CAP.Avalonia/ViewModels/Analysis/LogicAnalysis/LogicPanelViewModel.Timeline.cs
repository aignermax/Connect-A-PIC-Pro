using System.Collections.ObjectModel;
using CAP_Core.Analysis.LogicAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;

/// <summary>
/// Timeline half of <see cref="LogicPanelViewModel"/> (issue #1045, rung 5
/// visualizer slice 2): every input toggle hands the previous and next input
/// assignment to <see cref="LogicEventTimeline.Compute"/> and shows the returned
/// switch events — time, gate, pin, transition — in arrival order. This is the
/// first visible "watch your computer compute" slice: the panel consumes the
/// timeline data structure without recomputing any physics.
/// </summary>
public partial class LogicPanelViewModel
{
    private IReadOnlyDictionary<string, bool>? _previousInputBits;

    /// <summary>How many clock steps have appended their entries to the current timeline.</summary>
    private int _clockStepCount;

    /// <summary>
    /// The switch events of the last input toggle plus the entries of every clock
    /// step since (issue #1110), in <see cref="LogicEventTimeline"/> order (time,
    /// then gate id, then pin). Empty before the first toggle or step and after a
    /// toggle that changed no gate output.
    /// </summary>
    public ObservableCollection<LogicTimelineEventViewModel> TimelineEvents { get; } = new();

    /// <summary>True when the timeline holds at least one switch event.</summary>
    [ObservableProperty]
    private bool _hasTimelineEvents;

    /// <summary>
    /// Computes the switch events between the last shown input assignment and
    /// <paramref name="currentBits"/> and replaces the displayed timeline. The
    /// first call after a build only records the baseline — no toggle has happened
    /// yet, so the timeline stays empty. A re-evaluation with unchanged bits (the
    /// re-settle after a clock step) is no toggle at all: the timeline, including
    /// the step's entries, stays. A new toggle also exits replay: the badges
    /// already show the new live end state, and the before-bits of the previous
    /// toggle no longer describe the network.
    /// </summary>
    private void UpdateTimeline(IReadOnlyDictionary<string, bool> currentBits)
    {
        if (_network == null)
            return;
        if (_previousInputBits == null)
        {
            _previousInputBits = currentBits;
            return;
        }
        if (BitsEqual(_previousInputBits, currentBits))
            return;

        var events = LogicEventTimeline.Compute(_network, _previousInputBits, currentBits);
        _replayBeforeResult = _network.Evaluate(_previousInputBits);
        StopPlayback();
        SelectedTimelineEvent = null;
        TimelineEvents.Clear();
        foreach (var e in events)
            TimelineEvents.Add(new LogicTimelineEventViewModel(e));
        HasTimelineEvents = TimelineEvents.Count > 0;
        _previousInputBits = currentBits;
        _clockStepCount = 0;
        PreviousTimelineEventCommand.NotifyCanExecuteChanged();
        NextTimelineEventCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Appends one clock step's entries (issue #1110) behind the entries of the
    /// preceding settle phase: a "clock #k" divider opens the block, the commit
    /// entries land at the timeline's current end time, and the downstream ripple
    /// follows with non-decreasing times. A quiet clock (no committed output
    /// changed) leaves the timeline untouched. Replay stays consistent across the
    /// boundary: the before-state of an empty timeline is the pre-step settling
    /// <paramref name="preStepResult"/>; a timeline that already has entries keeps
    /// its original before-state, since the step's events continue from it.
    /// </summary>
    private void AppendClockStepEvents(
        IReadOnlyList<LogicSwitchEvent> stepEvents,
        IReadOnlyDictionary<string, bool> preStepResult)
    {
        if (stepEvents.Count == 0)
            return;
        if (TimelineEvents.Count == 0)
            _replayBeforeResult = preStepResult;
        _clockStepCount++;
        var offset = TimelineEvents.Count == 0 ? 0.0 : TimelineEvents[^1].Event.TimePicoseconds;
        var divider = string.Format(Translate("LogicPanel.ClockDivider"), _clockStepCount);
        var isBlockFirst = true;
        foreach (var e in stepEvents)
        {
            // The divider label rides on the block's first row — it is display
            // metadata, so the row itself stays a normal selectable event.
            TimelineEvents.Add(new LogicTimelineEventViewModel(
                e with { TimePicoseconds = e.TimePicoseconds + offset })
            {
                ClockBoundaryText = isBlockFirst ? divider : "",
            });
            isBlockFirst = false;
        }
        HasTimelineEvents = true;
        PreviousTimelineEventCommand.NotifyCanExecuteChanged();
        NextTimelineEventCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Restarts the timeline at the fresh settle phase of a register reset (issue
    /// #1127): the clock history and any replay artifact go, the clock counter
    /// returns to 0 (the next step opens "clock #1" again), and the reset's own
    /// commit/ripple entries become the whole timeline — a settle phase like the
    /// one after a toggle, so no clock divider. The before-state of the new
    /// timeline is the pre-reset settling <paramref name="preResetResult"/>, so
    /// replaying the reset's entries keeps the invariant of issue #1058. A quiet
    /// reset (every register already read 0) leaves the timeline empty.
    /// </summary>
    private void RestartTimelineAtFreshSettle(
        IReadOnlyList<LogicSwitchEvent> resetEvents,
        IReadOnlyDictionary<string, bool> preResetResult)
    {
        ClearTimeline();
        if (resetEvents.Count == 0)
            return;
        _replayBeforeResult = preResetResult;
        foreach (var e in resetEvents)
            TimelineEvents.Add(new LogicTimelineEventViewModel(e));
        HasTimelineEvents = true;
        PreviousTimelineEventCommand.NotifyCanExecuteChanged();
        NextTimelineEventCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Two input assignments are equal when every named bit matches.</summary>
    private static bool BitsEqual(
        IReadOnlyDictionary<string, bool> a, IReadOnlyDictionary<string, bool> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var bit) && bit == pair.Value);

    /// <summary>Discards the displayed timeline, the input baseline, and any active replay.</summary>
    private void ClearTimeline()
    {
        TimelineEvents.Clear();
        HasTimelineEvents = false;
        _previousInputBits = null;
        _clockStepCount = 0;
        ClearReplay();
    }
}
