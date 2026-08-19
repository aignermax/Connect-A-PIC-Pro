using CAP_Core.Analysis.LogicAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;

/// <summary>
/// One row of the Logic panel's event timeline (issue #1045): a single gate output
/// pin switching at one point in time after the last input toggle. Pure display
/// wrapper over <see cref="LogicSwitchEvent"/> — no physics is recomputed here,
/// the row carries exactly what <see cref="LogicEventTimeline.Compute"/> produced.
/// </summary>
public partial class LogicTimelineEventViewModel : ObservableObject
{
    private const string TimeFormat = "{0:0.0} ps";

    /// <summary>Wraps one switch event for display.</summary>
    public LogicTimelineEventViewModel(LogicSwitchEvent switchEvent)
    {
        Event = switchEvent;
        TimeText = string.Format(TimeFormat, switchEvent.TimePicoseconds);
        GatePinText = $"{switchEvent.GateId}.{switchEvent.OutputPin}";
    }

    /// <summary>The underlying switch event produced by <see cref="LogicEventTimeline.Compute"/>.</summary>
    public LogicSwitchEvent Event { get; }

    /// <summary>Absolute switch time as display text (e.g. "12.3 ps").</summary>
    public string TimeText { get; }

    /// <summary>Gate and pin in <c>&lt;gate&gt;.&lt;pin&gt;</c> form (e.g. "H1SUM1.Y").</summary>
    public string GatePinText { get; }

    /// <summary>True when the pin rises (0→1), false when it falls (1→0).</summary>
    public bool IsRising => Event.NewValue;

    /// <summary>The transition as display text ("0→1" or "1→0").</summary>
    public string TransitionText => IsRising ? "0→1" : "1→0";
}
