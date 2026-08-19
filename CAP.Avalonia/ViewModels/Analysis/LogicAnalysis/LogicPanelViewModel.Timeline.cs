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

    /// <summary>
    /// The switch events of the last input toggle, in <see cref="LogicEventTimeline"/>
    /// order (time, then gate id, then pin). Empty before the first toggle and after
    /// a toggle that changed no gate output.
    /// </summary>
    public ObservableCollection<LogicTimelineEventViewModel> TimelineEvents { get; } = new();

    /// <summary>True when the timeline holds at least one switch event.</summary>
    [ObservableProperty]
    private bool _hasTimelineEvents;

    /// <summary>
    /// Computes the switch events between the last shown input assignment and
    /// <paramref name="currentBits"/> and replaces the displayed timeline. The
    /// first call after a build only records the baseline — no toggle has happened
    /// yet, so the timeline stays empty. A new toggle also exits replay: the badges
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

        var events = LogicEventTimeline.Compute(_network, _previousInputBits, currentBits);
        _replayBeforeResult = _network.Evaluate(_previousInputBits);
        SelectedTimelineEvent = null;
        TimelineEvents.Clear();
        foreach (var e in events)
            TimelineEvents.Add(new LogicTimelineEventViewModel(e));
        HasTimelineEvents = TimelineEvents.Count > 0;
        _previousInputBits = currentBits;
        PreviousTimelineEventCommand.NotifyCanExecuteChanged();
        NextTimelineEventCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Discards the displayed timeline, the input baseline, and any active replay.</summary>
    private void ClearTimeline()
    {
        TimelineEvents.Clear();
        HasTimelineEvents = false;
        _previousInputBits = null;
        ClearReplay();
    }
}
