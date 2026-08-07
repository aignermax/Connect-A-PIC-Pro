namespace CAP.Avalonia.Services.GdsImport;

/// <summary>
/// Throttles per-item stage progress ("Placing components… 412/2700") for huge
/// GDS imports: the placement/connect loops run on the caller's (UI) context
/// and <see cref="IProgress{T}.Report"/> posts to that same dispatcher, so an
/// unthrottled per-item report floods the message loop (2700 placements =
/// 2700 queued callbacks — the dialog redraws the status line for every one).
/// The first item is always forwarded (immediate feedback), then at most one
/// message per configured interval, and ALWAYS the final count — the user sees
/// a live counter that provably ends at 100 %.
/// Same reporting style as the import service's save loop ("Saving components
/// to 'X'… N/M"), just time-throttled instead of count-throttled. The clock is
/// injectable for tests.
/// </summary>
internal sealed class GdsStageProgressReporter
{
    private readonly IProgress<string> _progress;
    private readonly string _stageName;
    private readonly TimeSpan _minInterval;
    private readonly Func<DateTimeOffset> _clock;
    private DateTimeOffset _lastReport = DateTimeOffset.MinValue;

    /// <summary>Initializes a new <see cref="GdsStageProgressReporter"/>.</summary>
    /// <param name="progress">Sink for the throttled messages.</param>
    /// <param name="stageName">Stage label the message starts with ("Placing components").</param>
    /// <param name="minInterval">Minimum time between forwarded messages.</param>
    /// <param name="clock">Time source; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public GdsStageProgressReporter(
        IProgress<string> progress,
        string stageName,
        TimeSpan minInterval,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentException.ThrowIfNullOrEmpty(stageName);
        ArgumentOutOfRangeException.ThrowIfLessThan(minInterval, TimeSpan.Zero);
        _progress = progress;
        _stageName = stageName;
        _minInterval = minInterval;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Reports <paramref name="done"/> of <paramref name="total"/> items completed:
    /// forwarded when it is the first call, when at least the configured interval
    /// passed since the last forwarded message, or when it is the final count.
    /// </summary>
    public void Report(int done, int total)
    {
        if (total <= 0)
            return;

        var now = _clock();
        var isFinal = done >= total;
        if (!isFinal && _lastReport != DateTimeOffset.MinValue && now - _lastReport < _minInterval)
            return;

        _lastReport = now;
        _progress.Report($"{_stageName}… {done}/{total}");
    }
}
