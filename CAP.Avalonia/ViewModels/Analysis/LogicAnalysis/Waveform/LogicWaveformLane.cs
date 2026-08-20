using CAP_Core.Analysis.LogicAnalysis;

namespace CAP.Avalonia.ViewModels.Analysis.LogicAnalysis.Waveform;

/// <summary>
/// One vertical edge of a waveform lane: the signal switches to
/// <see cref="NewLevel"/> at <see cref="TimePicoseconds"/>, drawn at the
/// normalized x position <see cref="XFraction"/> (0 = timeline start, 1 = end).
/// The renderer scales the fraction to pixels; tests assert the fractions
/// without touching a bitmap.
/// </summary>
/// <param name="XFraction">Normalized x position in [0, 1] — non-decreasing along a lane.</param>
/// <param name="TimePicoseconds">Absolute event time the edge belongs to.</param>
/// <param name="NewLevel">The level the signal switches to (true = high).</param>
public sealed record LogicWaveformEdge(double XFraction, double TimePicoseconds, bool NewLevel);

/// <summary>
/// One signal lane of the Logic panel's waveform strip (issue #1129): a named
/// signal's 0/1 step trace over the event timeline. The trace reads
/// <see cref="InitialLevel"/> from the timeline start until the first edge, then
/// follows every <see cref="LogicWaveformEdge"/> in order — one switch per event,
/// matching the timeline's one-switch-per-pin-per-phase model
/// (<see cref="LogicEventTimeline"/>). Pure display data: the mapper derives it
/// from the already-computed timeline, nothing is re-simulated.
/// </summary>
public sealed class LogicWaveformLane
{
    /// <summary>Initializes the lane with its name, resting level, and ordered edges.</summary>
    public LogicWaveformLane(
        string signalName,
        bool initialLevel,
        IReadOnlyList<LogicWaveformEdge> edges,
        bool liveLevel)
    {
        SignalName = signalName;
        InitialLevel = initialLevel;
        Edges = edges;
        LiveLevel = liveLevel;
    }

    /// <summary>
    /// The displayed signal name: the persisted signal name of a named input or
    /// output (<c>S̄</c>, <c>C0</c>), or the raw <c>&lt;gate&gt;.&lt;pin&gt;</c>
    /// id of a register output that carries no signal name.
    /// </summary>
    public string SignalName { get; }

    /// <summary>The level the lane shows from the timeline start until its first edge.</summary>
    public bool InitialLevel { get; }

    /// <summary>The lane's switch edges in timeline order — x fractions non-decreasing.</summary>
    public IReadOnlyList<LogicWaveformEdge> Edges { get; }

    /// <summary>The signal's settled level after the last timeline event (the live state).</summary>
    public bool LiveLevel { get; }

    /// <summary>
    /// The lane's level at a normalized x position: the initial level before the
    /// first edge, the newest edge's level from its position on (an edge applies
    /// at its own x — the step trace is closed to the right).
    /// </summary>
    public bool LevelAt(double xFraction)
    {
        var level = InitialLevel;
        foreach (var edge in Edges)
        {
            if (edge.XFraction > xFraction)
                break;
            level = edge.NewLevel;
        }
        return level;
    }
}
