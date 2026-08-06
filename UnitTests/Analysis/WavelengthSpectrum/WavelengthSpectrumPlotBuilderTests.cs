using System;
using System.Collections.Generic;
using System.Linq;
using CAP_Core.Analysis.WavelengthSpectrum;
using CAP.Avalonia.ViewModels.Analysis.WavelengthSpectrum;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.WavelengthSpectrum;

public class WavelengthSpectrumPlotBuilderTests
{
    private const double DesignWavelengthNm = 1550;

    private static TransmissionCurve CreateCurve(
        Guid? pinId = null, double level = 0.5, bool atFloor = false)
    {
        return new TransmissionCurve(
            pinId ?? Guid.NewGuid(),
            new double[] { 1500, 1550, 1600 },
            new[] { level, level, level },
            atFloor);
    }

    [Fact]
    public void BuildPlotModel_OneSeriesPerCurve_WithResolvedLegendTitles()
    {
        var pinA = Guid.NewGuid();
        var pinB = Guid.NewGuid();
        var curves = new[] { CreateCurve(pinA), CreateCurve(pinB, 0.3) };
        var labels = new Dictionary<Guid, string> { { pinA, "OutA.o1" }, { pinB, "OutB.o1" } };

        var model = WavelengthSpectrumPlotBuilder.BuildPlotModel(
            curves, id => labels[id], DesignWavelengthNm);

        model.Series.Count.ShouldBe(2);
        model.Series.Cast<LineSeries>().Select(s => s.Title)
            .ShouldBe(new[] { "OutA.o1", "OutB.o1" });
        model.Legends.ShouldNotBeEmpty();
    }

    [Fact]
    public void BuildPlotModel_MarksDesignWavelength_WhenInsideSweepRange()
    {
        var model = WavelengthSpectrumPlotBuilder.BuildPlotModel(
            new[] { CreateCurve() }, _ => null, DesignWavelengthNm);

        var marker = model.Annotations.OfType<LineAnnotation>().ShouldHaveSingleItem();
        marker.X.ShouldBe(DesignWavelengthNm);
        marker.Type.ShouldBe(LineAnnotationType.Vertical);
    }

    [Fact]
    public void BuildPlotModel_OmitsDesignWavelengthMarker_WhenOutsideSweepRange()
    {
        var model = WavelengthSpectrumPlotBuilder.BuildPlotModel(
            new[] { CreateCurve() }, _ => null, designWavelengthNm: 1310);

        model.Annotations.ShouldBeEmpty();
    }

    [Fact]
    public void BuildPlotModel_ScalesAxesToSweepRangeAndTransmission()
    {
        var model = WavelengthSpectrumPlotBuilder.BuildPlotModel(
            new[] { CreateCurve(level: 0.8) }, _ => null, DesignWavelengthNm);

        var xAxis = model.Axes.First(a => a.Position == AxisPosition.Bottom);
        xAxis.Minimum.ShouldBe(1500);
        xAxis.Maximum.ShouldBe(1600);
        xAxis.MajorStep.ShouldBe(20, 1e-12); // 100 nm range → round 20 nm ticks

        var yAxis = model.Axes.First(a => a.Position == AxisPosition.Left);
        yAxis.Minimum.ShouldBe(0);
        yAxis.Maximum.ShouldBe(0.8 * 1.05, 1e-12);
    }

    [Fact]
    public void BuildPlotModel_SkipsNoiseFloorCurves_WhenOthersCarryLight()
    {
        var litPin = Guid.NewGuid();
        var curves = new[]
        {
            CreateCurve(litPin, 0.5),
            CreateCurve(level: 0, atFloor: true),
        };

        var model = WavelengthSpectrumPlotBuilder.BuildPlotModel(
            curves, _ => null, DesignWavelengthNm);

        model.Series.Count.ShouldBe(1);
    }

    [Fact]
    public void BuildPlotModel_AllCurvesAtFloor_StillDrawsThem()
    {
        var curves = new[] { CreateCurve(level: 0, atFloor: true) };

        var model = WavelengthSpectrumPlotBuilder.BuildPlotModel(
            curves, _ => null, DesignWavelengthNm);

        model.Series.Count.ShouldBe(1);
    }

    [Fact]
    public void BuildPlotModel_CapsSeriesCount()
    {
        var curves = Enumerable.Range(0, 12).Select(_ => CreateCurve()).ToList();

        var model = WavelengthSpectrumPlotBuilder.BuildPlotModel(
            curves, _ => null, DesignWavelengthNm);

        model.Series.Count.ShouldBe(WavelengthSpectrumPlotBuilder.MaxSeries);
    }

    [Fact]
    public void BuildPlotModel_NoCurves_ReturnsEmptyModelWithoutThrowing()
    {
        var model = WavelengthSpectrumPlotBuilder.BuildPlotModel(
            Array.Empty<TransmissionCurve>(), _ => null, DesignWavelengthNm);

        model.Series.ShouldBeEmpty();
    }
}
