using CAP_Core.Analysis.EyeDiagram;
using CAP.Avalonia.Services.Localization;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace CAP.Avalonia.ViewModels.Analysis.EyeDiagram;

/// <summary>
/// Pure mapping helpers that turn an <see cref="EyeHistogram"/> into an OxyPlot
/// heat-map (persistence display). Mirrors <c>TimeTracePlotBuilder</c>'s dark
/// theme; kept free of ViewModel state so the histogram→plot mapping is testable.
/// </summary>
internal static class EyeDiagramPlotBuilder
{
    /// <summary>Seconds-to-picoseconds factor for the time axis.</summary>
    private const double SecondsToPicoseconds = 1e12;

    /// <summary>Number of colours in the heat-map palette.</summary>
    private const int PaletteSize = 200;

    private static readonly OxyColor PlotForeground = OxyColor.Parse("#E0E0E0");
    private static readonly OxyColor PlotAxisline = OxyColor.Parse("#808080");

    /// <summary>
    /// Builds the eye-diagram heat map: X = time offset within the bit period (ps),
    /// Y = power, colour = log-scaled sample count (persistence).
    /// </summary>
    /// <param name="histogram">Histogram produced by <see cref="EyeDiagramBuilder"/>.</param>
    public static PlotModel BuildPlotModel(EyeHistogram histogram)
    {
        var model = CreateEmptyPlotModel();

        // Log-scale the counts so faint traces stay visible next to dense rails.
        var data = new double[histogram.TimeBinCount, histogram.AmplitudeBinCount];
        for (int t = 0; t < histogram.TimeBinCount; t++)
            for (int a = 0; a < histogram.AmplitudeBinCount; a++)
                data[t, a] = Math.Log10(1 + histogram.Counts[t, a]);

        model.Axes.Add(new LinearColorAxis
        {
            Position = AxisPosition.Right,
            Palette = OxyPalettes.Hot(PaletteSize),
            LowColor = OxyColors.Black,
            IsAxisVisible = false,
        });

        model.Series.Add(new HeatMapSeries
        {
            X0 = 0,
            X1 = histogram.BitPeriodSeconds * SecondsToPicoseconds,
            Y0 = histogram.MinAmplitude,
            Y1 = histogram.MaxAmplitude,
            Data = data,
            Interpolate = true,
            RenderMethod = HeatMapRenderMethod.Bitmap,
        });

        model.InvalidatePlot(true);
        return model;
    }

    /// <summary>Creates an empty, dark-themed eye-diagram plot model with labelled axes.</summary>
    public static PlotModel CreateEmptyPlotModel()
    {
        var model = new PlotModel
        {
            Title = LocalizationService.Instance.Translate("Analysis.Eye.ChartTitle"),
            Background = OxyColors.Black,
            TextColor = PlotForeground,
            TitleColor = PlotForeground,
            PlotAreaBorderColor = PlotAxisline,
        };
        model.Axes.Add(CreateAxis(AxisPosition.Bottom, "Time in bit period (ps)"));
        model.Axes.Add(CreateAxis(AxisPosition.Left, "Power |E(t)|²"));
        return model;
    }

    private static LinearAxis CreateAxis(AxisPosition position, string title) => new()
    {
        Position = position,
        Title = title,
        TextColor = PlotForeground,
        TitleColor = PlotForeground,
        TicklineColor = PlotAxisline,
        AxislineColor = PlotAxisline,
        AxislineStyle = LineStyle.Solid,
    };
}
