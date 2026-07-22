using CAP_Core.LightCalculation.TimeDomainSimulation;
using CAP.Avalonia.Controls.Plotting;
using CAP.Avalonia.Services.Localization;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Legends;

namespace CAP.Avalonia.ViewModels.Analysis.TimeTrace;

/// <summary>
/// Pure mapping helpers that turn a <see cref="TimeDomainResult"/> into an
/// OxyPlot <see cref="PlotModel"/> of power-vs-time traces (one series per
/// output pin) and into the legend/toggle items that drive visibility.
/// Mirrors the ONA spectrum charting approach (#526) for consistency.
/// Kept free of ViewModel/UI state so the result→series mapping is unit-testable.
/// </summary>
internal static class TimeTracePlotBuilder
{
    /// <summary>Seconds-to-picoseconds factor for the time axis.</summary>
    private const double SecondsToPicoseconds = 1e12;

    /// <summary>Seconds-to-nanoseconds factor for the time axis.</summary>
    private const double SecondsToNanoseconds = 1e9;

    /// <summary>
    /// Windows of at least this many picoseconds are plotted in ns instead of
    /// ps, so a PRBS run reads "1.6 ns" rather than an easily-misread "1600".
    /// </summary>
    private const double NanosecondThresholdPs = 1000;

    /// <summary>Power below this (field-units²) is treated as "no signal" and the pin is skipped.</summary>
    private const double SignalFloor = 1e-12;

    private const double SeriesStrokeThickness = 1.5;

    // Light colours readable on the dark (#1e1e1e) panel background. Matches
    // the palette feel of the ONA chart while staying distinct per series.
    private static readonly OxyColor[] Palette =
    {
        OxyColor.Parse("#4FC3F7"), OxyColor.Parse("#FF8A65"),
        OxyColor.Parse("#81C784"), OxyColor.Parse("#BA68C8"),
        OxyColor.Parse("#FFD54F"), OxyColor.Parse("#4DD0E1"),
        OxyColor.Parse("#F06292"), OxyColor.Parse("#AED581"),
    };

    private static readonly OxyColor PlotForeground = OxyColor.Parse("#E0E0E0");
    private static readonly OxyColor PlotGridline = OxyColor.Parse("#404040");
    private static readonly OxyColor PlotAxisline = OxyColor.Parse("#808080");

    /// <summary>
    /// Builds the legend/toggle items for a result: one per output pin that
    /// carries a non-negligible signal. Each gets a stable palette colour and a
    /// resolved display label. Pins are ordered by descending peak power so the
    /// strongest traces appear first.
    /// </summary>
    /// <param name="result">The transient result to map.</param>
    /// <param name="resolveLabel">Maps a pin Guid to a display label; may return null.</param>
    public static IReadOnlyList<TimeTraceSeriesViewModel> BuildSeriesItems(
        TimeDomainResult result, Func<Guid, string?> resolveLabel)
    {
        var ordered = result.PinTraces
            .Where(kv => kv.Value.Length > 0 && kv.Value.Max() >= SignalFloor)
            .OrderByDescending(kv => kv.Value.Max())
            .ToList();

        var items = new List<TimeTraceSeriesViewModel>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
        {
            var pinId = ordered[i].Key;
            var label = resolveLabel(pinId) ?? $"Pin {pinId.ToString("N")[..6]}";
            items.Add(new TimeTraceSeriesViewModel(pinId, label, Palette[i % Palette.Length]));
        }
        return items;
    }

    /// <summary>
    /// Builds a <see cref="PlotModel"/> from the result, drawing one line series
    /// per <paramref name="seriesItems"/> entry whose
    /// <see cref="TimeTraceSeriesViewModel.IsVisible"/> is true. The X axis is
    /// time in picoseconds; the Y axis is power |E(t)|². Zoom/pan are enabled by
    /// OxyPlot's default linear-axis behaviour.
    /// </summary>
    /// <param name="result">The transient result supplying the time axis and traces.</param>
    /// <param name="seriesItems">Legend items (identity + colour + visibility).</param>
    public static PlotModel BuildPlotModel(
        TimeDomainResult result, IReadOnlyList<TimeTraceSeriesViewModel> seriesItems)
    {
        var model = CreateEmptyPlotModel();

        // Pick the unit from the window length so long (data) runs read in ns.
        double windowPs = result.TimeAxis.Length > 0
            ? result.TimeAxis[^1] * SecondsToPicoseconds
            : 0;
        bool useNs = windowPs >= NanosecondThresholdPs;
        double scale = useNs ? SecondsToNanoseconds : SecondsToPicoseconds;
        string unit = useNs ? "ns" : "ps";
        model.Axes.First(a => a.Position == AxisPosition.Bottom).Title =
            string.Format(LocalizationService.Instance.Translate("Analysis.TimeDomain.TimeAxis"), unit);

        var scaledTime = result.TimeAxis.Select(t => t * scale).ToArray();

        foreach (var item in seriesItems)
        {
            if (!item.IsVisible) continue;
            if (!result.PinTraces.TryGetValue(item.PinId, out var trace)) continue;

            var series = new XTrackingLineSeries
            {
                Title = item.Label,
                Color = item.Color,
                StrokeThickness = SeriesStrokeThickness,
                CanTrackerInterpolatePoints = true,
                TrackerTextProvider = dp => $"{item.Label}\nt = {dp.X:0.000} {unit}\nP = {dp.Y:0.000e0}",
            };

            int count = Math.Min(scaledTime.Length, trace.Length);
            for (int n = 0; n < count; n++)
                series.Points.Add(new DataPoint(scaledTime[n], trace[n]));

            model.Series.Add(series);
        }

        model.InvalidatePlot(true);
        return model;
    }

    /// <summary>Creates an empty, dark-themed time-trace plot model with labelled axes.</summary>
    public static PlotModel CreateEmptyPlotModel()
    {
        var model = new PlotModel
        {
            Title = LocalizationService.Instance.Translate("Analysis.TimeDomain.ChartTitle"),
            Background = OxyColors.Transparent,
            TextColor = PlotForeground,
            TitleColor = PlotForeground,
            PlotAreaBorderColor = PlotAxisline,
        };
        // The per-pin checkbox list below the chart (TimeDomainPanel.axaml) is the only
        // series-visibility control; an in-plot legend duplicated it as clickable buttons
        // cluttering the chart (field feedback, round 5 finding 1). Kept (not removed) so a
        // future need for the in-plot legend is a one-line flip, not a re-derivation.
        model.Legends.Add(new Legend
        {
            LegendPosition = LegendPosition.RightTop,
            LegendTextColor = PlotForeground,
            IsLegendVisible = false,
        });
        model.Axes.Add(CreateAxis(AxisPosition.Bottom,
            string.Format(LocalizationService.Instance.Translate("Analysis.TimeDomain.TimeAxis"), "ps")));
        model.Axes.Add(CreateAxis(AxisPosition.Left, "Power |E(t)|²"));
        return model;
    }

    private static LinearAxis CreateAxis(AxisPosition position, string title) => new()
    {
        Position = position,
        Title = title,
        MajorGridlineStyle = LineStyle.Dot,
        MajorGridlineColor = PlotGridline,
        TextColor = PlotForeground,
        TitleColor = PlotForeground,
        TicklineColor = PlotAxisline,
        AxislineColor = PlotAxisline,
        AxislineStyle = LineStyle.Solid,
    };
}
