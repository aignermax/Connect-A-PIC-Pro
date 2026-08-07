using CAP_Core.Analysis.WavelengthSpectrum;
using CAP.Avalonia.Controls.Plotting;
using CAP.Avalonia.Services.Localization;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Legends;

namespace CAP.Avalonia.ViewModels.Analysis.WavelengthSpectrum;

/// <summary>
/// Pure mapping helpers that turn <see cref="TransmissionCurve"/>s into an
/// OxyPlot transmission-vs-wavelength spectrum: one line series per output pin,
/// a legend with pin names and a dashed marker at the design wavelength.
/// Mirrors <c>TimeTracePlotBuilder</c>'s dark theme; kept free of ViewModel
/// state so the curves→plot mapping is unit-testable.
/// </summary>
internal static class WavelengthSpectrumPlotBuilder
{
    /// <summary>Maximum number of curves drawn, to keep the plot readable.</summary>
    public const int MaxSeries = 8;

    private const double SeriesStrokeThickness = 1.5;

    // Light colours readable on the dark panel background; same palette family
    // as the Transient chart so multi-pin colours feel consistent across tabs.
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
    private static readonly OxyColor DesignWavelengthColor = OxyColor.Parse("#E5C07B");

    /// <summary>
    /// Builds the spectrum plot: X = wavelength (nm), Y = linear transmission |S|²,
    /// one curve per output pin, legend with pin names, dashed vertical marker at
    /// the design wavelength. Curves stuck at the noise floor are skipped unless
    /// ALL curves are — then everything is drawn so the user sees a flat line
    /// rather than an empty chart.
    /// </summary>
    /// <param name="curves">Curves produced by <see cref="TransmissionSpectrumBuilder"/>.</param>
    /// <param name="resolveLabel">Maps a pin Guid to a display label; may return null.</param>
    /// <param name="designWavelengthNm">Design wavelength to mark (marker drawn only when inside the sweep range).</param>
    public static PlotModel BuildPlotModel(
        IReadOnlyList<TransmissionCurve> curves,
        Func<Guid, string?> resolveLabel,
        double designWavelengthNm)
    {
        var model = CreateEmptyPlotModel();
        var visible = SelectVisibleCurves(curves);
        if (visible.Count == 0)
        {
            model.InvalidatePlot(true);
            return model;
        }

        ConfigureAxes(model, visible);
        AddDesignWavelengthMarker(model, visible[0], designWavelengthNm);

        for (int i = 0; i < visible.Count; i++)
            model.Series.Add(CreateSeries(visible[i], resolveLabel, Palette[i % Palette.Length]));

        model.InvalidatePlot(true);
        return model;
    }

    /// <summary>Creates an empty, dark-themed spectrum plot model with labelled axes and legend.</summary>
    public static PlotModel CreateEmptyPlotModel()
    {
        var model = new PlotModel
        {
            Title = LocalizationService.Instance.Translate("Analysis.Spectrum.ChartTitle"),
            Background = OxyColors.Transparent,
            TextColor = PlotForeground,
            TitleColor = PlotForeground,
            PlotAreaBorderColor = PlotAxisline,
        };
        model.Legends.Add(new Legend
        {
            LegendPosition = LegendPosition.RightTop,
            LegendTextColor = PlotForeground,
            LegendBackground = OxyColor.FromAColor(160, OxyColors.Black),
            LegendBorder = PlotAxisline,
        });
        model.Axes.Add(CreateAxis(AxisPosition.Bottom, "Wavelength (nm)"));
        model.Axes.Add(CreateAxis(AxisPosition.Left, "Transmission |S|²"));
        return model;
    }

    private static IReadOnlyList<TransmissionCurve> SelectVisibleCurves(
        IReadOnlyList<TransmissionCurve> curves)
    {
        var lit = curves.Where(c => !c.IsAtNoiseFloor).ToList();
        var selected = lit.Count > 0 ? lit : curves.ToList();
        return selected.Take(MaxSeries).ToList();
    }

    private static XTrackingLineSeries CreateSeries(
        TransmissionCurve curve, Func<Guid, string?> resolveLabel, OxyColor color)
    {
        var label = resolveLabel(curve.PinId) ?? $"Pin {curve.PinId.ToString("N")[..6]}";
        var series = new XTrackingLineSeries
        {
            Title = label,
            Color = color,
            StrokeThickness = SeriesStrokeThickness,
            CanTrackerInterpolatePoints = true,
            TrackerTextProvider = dp => $"{label}\nλ = {dp.X:0} nm\nT = {dp.Y:0.000}",
        };
        for (int i = 0; i < curve.WavelengthsNm.Count; i++)
            series.Points.Add(new DataPoint(curve.WavelengthsNm[i], curve.Transmission[i]));
        return series;
    }

    private static void ConfigureAxes(PlotModel model, IReadOnlyList<TransmissionCurve> curves)
    {
        double minNm = curves[0].WavelengthsNm[0];
        double maxNm = curves[0].WavelengthsNm[^1];
        var xAxis = (LinearAxis)model.Axes.First(a => a.Position == AxisPosition.Bottom);
        xAxis.Minimum = minNm;
        xAxis.Maximum = maxNm;
        xAxis.MajorStep = SpectrumAxisScaler.NiceTickStep(minNm, maxNm);

        double yMax = SpectrumAxisScaler.TransmissionAxisMax(curves);
        var yAxis = (LinearAxis)model.Axes.First(a => a.Position == AxisPosition.Left);
        yAxis.Minimum = 0;
        yAxis.Maximum = yMax;
        yAxis.MajorStep = SpectrumAxisScaler.NiceTickStep(0, yMax);
    }

    private static void AddDesignWavelengthMarker(
        PlotModel model, TransmissionCurve reference, double designWavelengthNm)
    {
        double minNm = reference.WavelengthsNm[0];
        double maxNm = reference.WavelengthsNm[^1];
        if (designWavelengthNm < minNm || designWavelengthNm > maxNm) return;

        model.Annotations.Add(new LineAnnotation
        {
            Type = LineAnnotationType.Vertical,
            X = designWavelengthNm,
            Color = DesignWavelengthColor,
            LineStyle = LineStyle.Dash,
            Text = $"λ₀ = {designWavelengthNm:0} nm",
            TextColor = DesignWavelengthColor,
            TextOrientation = AnnotationTextOrientation.Vertical,
        });
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
