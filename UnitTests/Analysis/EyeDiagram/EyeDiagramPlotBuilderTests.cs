using CAP_Core.Analysis.EyeDiagram;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Analysis.EyeDiagram;
using OxyPlot.Series;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.EyeDiagram;

/// <summary>
/// Locks in the eye/BER heat-map plot's chrome (Issue round-5 finding 1): unlike a
/// multi-series line chart, a single heat map has no per-series legend to show, so it
/// must stay legend-free — consistent with the transient chart, whose in-plot legend was
/// removed because the checkbox list beneath it already controls series visibility.
/// </summary>
public class EyeDiagramPlotBuilderTests
{
    public EyeDiagramPlotBuilderTests()
    {
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);
    }

    [Fact]
    public void CreateEmptyPlotModel_HasNoInPlotLegend()
    {
        var model = EyeDiagramPlotBuilder.CreateEmptyPlotModel();

        model.Legends.ShouldBeEmpty();
    }

    [Fact]
    public void CreateEmptyPlotModel_HasTimeAndPowerAxes()
    {
        var model = EyeDiagramPlotBuilder.CreateEmptyPlotModel();

        model.Series.Count.ShouldBe(0);
        model.Axes.Any(a => a.Title.Contains("Time")).ShouldBeTrue();
        model.Axes.Any(a => a.Title.Contains("Power")).ShouldBeTrue();
    }

    [Fact]
    public void BuildPlotModel_HasNoInPlotLegend()
    {
        var histogram = EyeDiagramBuilder.Build(
            new[] { 0.0, 0.9, 0.1, 0.8 }, sampleRateHz: 1e12, bitPeriodSeconds: 2e-12, skipBits: 0);

        var model = EyeDiagramPlotBuilder.BuildPlotModel(histogram);

        model.Legends.ShouldBeEmpty();
        model.Series.OfType<HeatMapSeries>().Count().ShouldBe(1);
    }
}
