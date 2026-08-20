namespace CAP.Avalonia.ViewModels.Analysis.LogicAnalysis.Waveform;

/// <summary>
/// One clock boundary of the waveform strip (issue #1129): the timeline's
/// "── clock #k ──" divider drawn as a thin vertical line across all lanes at
/// the normalized x position <see cref="XFraction"/>, carrying the same
/// (already localized) label the timeline list shows.
/// </summary>
/// <param name="XFraction">Normalized x position in [0, 1].</param>
/// <param name="TimePicoseconds">Absolute time of the clock block's first entry.</param>
/// <param name="Label">The divider label of the timeline row that opens the block.</param>
public sealed record LogicWaveformClockDivider(double XFraction, double TimePicoseconds, string Label);

/// <summary>
/// The display model behind the Logic panel's waveform strip (issue #1129, rung 5
/// visualizer): one step-trace lane per named signal, the clock boundaries as
/// vertical lines, and the replay cursor position. A pure function of the panel's
/// existing timeline data — <see cref="LogicWaveformMapper"/> rebuilds it whenever
/// the timeline, the live levels, or the replayed instant change. The x axis is
/// normalized to [0, 1] over [<see cref="StartTimePicoseconds"/>,
/// <see cref="EndTimePicoseconds"/>]; the strip does not scroll or zoom (slice 1).
/// </summary>
public sealed class LogicWaveformModel
{
    /// <summary>Initializes the model with its lanes, dividers, cursor, and time range.</summary>
    public LogicWaveformModel(
        IReadOnlyList<LogicWaveformLane> lanes,
        IReadOnlyList<LogicWaveformClockDivider> dividers,
        double? cursorXFraction,
        double startTimePicoseconds,
        double endTimePicoseconds)
    {
        Lanes = lanes;
        Dividers = dividers;
        CursorXFraction = cursorXFraction;
        StartTimePicoseconds = startTimePicoseconds;
        EndTimePicoseconds = endTimePicoseconds;
    }

    /// <summary>
    /// The signal lanes in display order: named network inputs first, then the named
    /// output taps, then register outputs not already covered by a named tap.
    /// </summary>
    public IReadOnlyList<LogicWaveformLane> Lanes { get; }

    /// <summary>The clock boundaries in timeline order — one per clocked step that appended entries.</summary>
    public IReadOnlyList<LogicWaveformClockDivider> Dividers { get; }

    /// <summary>
    /// Normalized x of the replay cursor (the replayed instant t_k), or null when
    /// the panel shows the live end state instead of replaying.
    /// </summary>
    public double? CursorXFraction { get; }

    /// <summary>The timeline start in picoseconds (always 0 — the input toggle instant).</summary>
    public double StartTimePicoseconds { get; }

    /// <summary>
    /// The timeline end in picoseconds: the last event's time. Strictly greater than
    /// <see cref="StartTimePicoseconds"/> — a degenerate single-instant timeline is
    /// widened so the x normalization never divides by zero.
    /// </summary>
    public double EndTimePicoseconds { get; }
}
