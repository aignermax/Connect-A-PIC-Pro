using CAP_Core.Analysis.LogicAnalysis;

namespace CAP.Avalonia.ViewModels.Analysis.LogicAnalysis.Waveform;

/// <summary>
/// One signal the waveform strip draws a lane for (issue #1129): its display name,
/// the gate output pin whose timeline events drive its edges (null for a network
/// input — inputs hold their toggled level through the whole timeline window, so
/// the lane is a flat trace), and its settled live level.
/// </summary>
/// <param name="SignalName">The lane's display name.</param>
/// <param name="Pin">The tapped gate output pin, or null for a network input lane.</param>
/// <param name="LiveLevel">The signal's level at the timeline's end (the live state).</param>
public sealed record LogicWaveformLaneSource(string SignalName, LogicPinRef? Pin, bool LiveLevel);

/// <summary>
/// Maps the Logic panel's event timeline onto the waveform strip's lanes (issue
/// #1129, rung 5 visualizer): every named signal becomes a step trace whose edges
/// are exactly the timeline entries of its pin, the "── clock #k ──" row labels
/// become vertical dividers, and the replayed instant becomes the cursor. The x
/// axis spans the timeline's [0, last event time] normalized to [0, 1] — a lane
/// with no events rests at its live level, a lane's level before its first event
/// is the opposite of that event's new level (the first switch is also the first
/// deviation from the timeline's start state, because the timeline records one
/// switch per pin per phase and phases append in time order). Pure mapping over
/// data the panel already shows — no evaluation, no new physics.
/// </summary>
public static class LogicWaveformMapper
{
    /// <summary>
    /// Builds the waveform model of the displayed timeline.
    /// </summary>
    /// <param name="sources">The named signals to draw, in display order.</param>
    /// <param name="timeline">The timeline rows, in arrival order (may be empty).</param>
    /// <param name="cursorTimePicoseconds">The replayed instant, or null at the live end state.</param>
    /// <returns>The lanes, dividers, and cursor with normalized x positions.</returns>
    public static LogicWaveformModel Build(
        IReadOnlyList<LogicWaveformLaneSource> sources,
        IReadOnlyList<LogicTimelineEventViewModel> timeline,
        double? cursorTimePicoseconds)
    {
        if (sources == null) throw new ArgumentNullException(nameof(sources));
        if (timeline == null) throw new ArgumentNullException(nameof(timeline));

        var start = 0.0;
        var end = timeline.Count == 0 ? 1.0 : timeline.Max(row => row.Event.TimePicoseconds);
        if (end <= start)
            end = start + 1.0;
        var range = end - start;
        double Fraction(double time) => (time - start) / range;

        var lanes = sources.Select(source => BuildLane(source, timeline, Fraction)).ToList();
        var dividers = timeline
            .Where(row => row.HasClockBoundary)
            .Select(row => new LogicWaveformClockDivider(
                Fraction(row.Event.TimePicoseconds), row.Event.TimePicoseconds, row.ClockBoundaryText))
            .ToList();
        return new LogicWaveformModel(
            lanes,
            dividers,
            cursorTimePicoseconds.HasValue ? Fraction(cursorTimePicoseconds.Value) : null,
            start,
            end);
    }

    /// <summary>
    /// One lane: the source's pin's events in timeline order become its edges. The
    /// level before the first edge is the opposite of that edge's new level; a lane
    /// without events rests at its live level for the whole window.
    /// </summary>
    private static LogicWaveformLane BuildLane(
        LogicWaveformLaneSource source,
        IReadOnlyList<LogicTimelineEventViewModel> timeline,
        Func<double, double> fraction)
    {
        if (source.Pin == null)
        {
            return new LogicWaveformLane(
                source.SignalName, source.LiveLevel, Array.Empty<LogicWaveformEdge>(), source.LiveLevel);
        }
        var pin = source.Pin;
        var edges = timeline
            .Where(row => row.Event.GateId == pin.GateId && row.Event.OutputPin == pin.PinName)
            .Select(row => new LogicWaveformEdge(
                fraction(row.Event.TimePicoseconds), row.Event.TimePicoseconds, row.Event.NewValue))
            .ToList();
        var initial = edges.Count > 0 ? !edges[0].NewLevel : source.LiveLevel;
        return new LogicWaveformLane(source.SignalName, initial, edges, source.LiveLevel);
    }
}
