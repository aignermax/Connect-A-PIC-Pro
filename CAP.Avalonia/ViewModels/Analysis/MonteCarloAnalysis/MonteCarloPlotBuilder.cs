using CAP_Core.Analysis.MonteCarloAnalysis;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace CAP.Avalonia.ViewModels.Analysis.MonteCarloAnalysis;

/// <summary>
/// Builds the OxyPlot models for the Monte-Carlo panel: a spectral envelope
/// (nominal curve + p5–p95 band + min/max extremes) and an eye-openness
/// histogram — deliberately NOT an overplot of all N curves.
/// </summary>
internal static class MonteCarloPlotBuilder
{
    private const double LowerPercentile = 5;
    private const double UpperPercentile = 95;

    // Light colors so the plot reads on the dark (#1e1e1e) dock background.
    private static readonly OxyColor Foreground = OxyColor.Parse("#E0E0E0");
    private static readonly OxyColor Gridline = OxyColor.Parse("#404040");
    private static readonly OxyColor Axisline = OxyColor.Parse("#808080");
    private static readonly OxyColor NominalColor = OxyColor.Parse("#61AFEF");
    private static readonly OxyColor BandColor = OxyColor.FromAColor(70, OxyColor.Parse("#61AFEF"));
    private static readonly OxyColor ExtremeColor = OxyColor.Parse("#707070");
    private static readonly OxyColor HistogramColor = OxyColor.Parse("#98C379");

    /// <summary>Empty placeholder model shown before the first run.</summary>
    public static PlotModel CreateEmptyModel(string title)
        => CreateBaseModel(title, "Wavelength (nm)", "Insertion Loss (dB)");

    /// <summary>Envelope plot: nominal curve, p5–p95 percentile band, min/max extremes.</summary>
    public static PlotModel BuildEnvelopePlot(int[] wavelengths, MonteCarloResult result, string pinName)
    {
        var model = CreateBaseModel(
            $"Fabrication spread — {pinName} ({result.RunCurves.Count} runs)",
            "Wavelength (nm)", "Insertion Loss (dB)");

        var lower = result.GetPercentileCurve(LowerPercentile);
        var upper = result.GetPercentileCurve(UpperPercentile);
        var band = new AreaSeries
        {
            Title = $"p{LowerPercentile:0}–p{UpperPercentile:0}",
            Color = BandColor,
            Fill = BandColor,
            StrokeThickness = 0,
        };
        for (int i = 0; i < wavelengths.Length; i++)
        {
            band.Points.Add(new DataPoint(wavelengths[i], upper[i]));
            band.Points2.Add(new DataPoint(wavelengths[i], lower[i]));
        }
        model.Series.Add(band);

        model.Series.Add(CreateCurve("Min", result.GetMinCurve(), wavelengths, ExtremeColor, LineStyle.Dash, 1));
        model.Series.Add(CreateCurve("Max", result.GetMaxCurve(), wavelengths, ExtremeColor, LineStyle.Dash, 1));
        model.Series.Add(CreateCurve(
            "Nominal", result.NominalCurve.ToArray(), wavelengths, NominalColor, LineStyle.Solid, 2));

        model.IsLegendVisible = true;
        return model;
    }

    /// <summary>Histogram of the eye-openness distribution with the nominal value marked.</summary>
    public static PlotModel BuildHistogramPlot(DistributionHistogram histogram, double nominalValue)
    {
        var model = CreateBaseModel("Eye-openness distribution", "Eye height", "Runs");

        var bars = new RectangleBarSeries { FillColor = HistogramColor, StrokeThickness = 0 };
        for (int bin = 0; bin < histogram.BinCounts.Count; bin++)
        {
            double x0 = histogram.MinValue + bin * histogram.BinWidth;
            bars.Items.Add(new RectangleBarItem(x0, 0, x0 + histogram.BinWidth, histogram.BinCounts[bin]));
        }
        model.Series.Add(bars);

        model.Annotations.Add(new LineAnnotation
        {
            Type = LineAnnotationType.Vertical,
            X = nominalValue,
            Color = NominalColor,
            StrokeThickness = 2,
            Text = "nominal",
            TextColor = Foreground,
        });
        return model;
    }

    private static LineSeries CreateCurve(
        string title, double[] values, int[] wavelengths,
        OxyColor color, LineStyle style, double thickness)
    {
        var series = new LineSeries
        {
            Title = title,
            Color = color,
            LineStyle = style,
            StrokeThickness = thickness,
        };
        for (int i = 0; i < wavelengths.Length; i++)
            series.Points.Add(new DataPoint(wavelengths[i], values[i]));
        return series;
    }

    private static PlotModel CreateBaseModel(string title, string xTitle, string yTitle)
    {
        var model = new PlotModel
        {
            Title = title,
            TitleFontSize = 12,
            Background = OxyColors.Transparent,
            TextColor = Foreground,
            TitleColor = Foreground,
            PlotAreaBorderColor = Axisline,
        };
        model.Axes.Add(CreateAxis(AxisPosition.Bottom, xTitle));
        model.Axes.Add(CreateAxis(AxisPosition.Left, yTitle));
        return model;
    }

    private static LinearAxis CreateAxis(AxisPosition position, string title) => new()
    {
        Position = position,
        Title = title,
        MajorGridlineStyle = LineStyle.Dot,
        MajorGridlineColor = Gridline,
        TextColor = Foreground,
        TitleColor = Foreground,
        TicklineColor = Axisline,
        AxislineColor = Axisline,
        AxislineStyle = LineStyle.Solid,
    };
}
