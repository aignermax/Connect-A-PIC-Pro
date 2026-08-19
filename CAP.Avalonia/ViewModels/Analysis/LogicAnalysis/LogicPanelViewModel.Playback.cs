using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;

/// <summary>
/// Auto-play half of <see cref="LogicPanelViewModel"/> (issue #1069, rung 5 visualizer
/// slice 4): the Play button turns the steppable replay into the first self-running
/// "watch your computer compute" animation. Play starts from the current selection
/// (or the first event) and every tick advances the replayed instant one switch
/// event; the tick after the last event ends playback and returns the canvas to the
/// live end state. Pause freezes mid-ripple; any manual interaction — selecting or
/// deselecting a row, stepping, a new input toggle, a design edit — stops playback.
/// The ViewModel stays timer-free so tests advance ticks synchronously: the view
/// wires <see cref="AdvancePlaybackTick"/> to a DispatcherTimer at
/// <see cref="PlaybackInterval"/>. The cadence is didactic, not to scale — the real
/// picosecond gaps are far below perception.
/// </summary>
public partial class LogicPanelViewModel
{
    /// <summary>
    /// Wall-clock cadence of the auto-play. Fixed and deliberately not the
    /// picosecond scale — the timeline help says so.
    /// </summary>
    public static TimeSpan PlaybackInterval { get; } = TimeSpan.FromMilliseconds(600);

    /// <summary>True while the ripple advances on its own (button shows Pause).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseText))]
    private bool _isPlaying;

    /// <summary>Label of the Play/Pause button — Play at rest, Pause while the ripple runs.</summary>
    public string PlayPauseText =>
        Translate(IsPlaying ? "LogicPanel.PlaybackPause" : "LogicPanel.PlaybackPlay");

    /// <summary>
    /// Play starts the auto-play from the current selection (or the first event);
    /// Pause freezes the replayed instant.
    /// </summary>
    [RelayCommand]
    private void TogglePlayback()
    {
        if (IsPlaying)
        {
            StopPlayback();
            return;
        }
        if (TimelineEvents.Count == 0)
            return;
        IsPlaying = true;
        SelectedTimelineEvent ??= TimelineEvents[0];
    }

    /// <summary>
    /// One playback step: advances the replayed instant to the next switch event.
    /// The tick after the last event stops playback and returns the canvas to the
    /// live end state. Called by the view's DispatcherTimer; tests call it directly.
    /// </summary>
    public void AdvancePlaybackTick()
    {
        if (!IsPlaying)
            return;
        var next = SelectedIndex + 1;
        if (next < TimelineEvents.Count)
        {
            StepTo(next);
            return;
        }
        StopPlayback();
        SelectedTimelineEvent = null;
    }

    /// <summary>Stops the auto-play; the replayed instant stays where it is (Pause freezes).</summary>
    private void StopPlayback() => IsPlaying = false;
}
