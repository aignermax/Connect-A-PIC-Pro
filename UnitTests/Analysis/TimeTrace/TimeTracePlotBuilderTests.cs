using System;
using System.Collections.Generic;
using System.Linq;
using CAP_Core.LightCalculation.TimeDomainSimulation;
using CAP.Avalonia.ViewModels.Analysis.TimeTrace;
using OxyPlot.Series;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.TimeTrace;

/// <summary>
/// Tests the result→series mapping and visibility-toggle logic that drives the
/// transient waveform plot (Issue #601).
/// </summary>
public class TimeTracePlotBuilderTests
{
    private static TimeDomainResult MakeResult(
        double[] timeAxis, params (Guid pin, double[] trace)[] traces)
    {
        var dict = traces.ToDictionary(t => t.pin, t => t.trace);
        return new TimeDomainResult(timeAxis, dict);
    }

    [Fact]
    public void BuildSeriesItems_CreatesOneItemPerSignalCarryingPin()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var result = MakeResult(
            new[] { 0.0, 1e-12, 2e-12 },
            (a, new[] { 0.0, 0.5, 0.2 }),
            (b, new[] { 0.0, 0.9, 0.1 }));

        var items = TimeTracePlotBuilder.BuildSeriesItems(result, _ => null);

        items.Count.ShouldBe(2);
        items.Select(i => i.PinId).ShouldBe(new[] { a, b }, ignoreOrder: true);
    }

    [Fact]
    public void BuildSeriesItems_SkipsPinsBelowSignalFloor()
    {
        var loud = Guid.NewGuid();
        var silent = Guid.NewGuid();
        var result = MakeResult(
            new[] { 0.0, 1e-12 },
            (loud, new[] { 0.0, 0.5 }),
            (silent, new[] { 0.0, 1e-15 }));

        var items = TimeTracePlotBuilder.BuildSeriesItems(result, _ => null);

        items.Count.ShouldBe(1);
        items[0].PinId.ShouldBe(loud);
    }

    [Fact]
    public void BuildSeriesItems_OrdersByDescendingPeakPower()
    {
        var weak = Guid.NewGuid();
        var strong = Guid.NewGuid();
        var result = MakeResult(
            new[] { 0.0, 1e-12 },
            (weak, new[] { 0.0, 0.2 }),
            (strong, new[] { 0.0, 0.9 }));

        var items = TimeTracePlotBuilder.BuildSeriesItems(result, _ => null);

        items[0].PinId.ShouldBe(strong);
        items[1].PinId.ShouldBe(weak);
    }

    [Fact]
    public void BuildSeriesItems_UsesResolvedLabelWhenAvailable()
    {
        var pin = Guid.NewGuid();
        var result = MakeResult(new[] { 0.0, 1e-12 }, (pin, new[] { 0.0, 0.5 }));

        var items = TimeTracePlotBuilder.BuildSeriesItems(result, _ => "MMI.out1");

        items[0].Label.ShouldBe("MMI.out1");
    }

    [Fact]
    public void BuildSeriesItems_FallsBackToShortGuidLabel()
    {
        var pin = Guid.NewGuid();
        var result = MakeResult(new[] { 0.0, 1e-12 }, (pin, new[] { 0.0, 0.5 }));

        var items = TimeTracePlotBuilder.BuildSeriesItems(result, _ => null);

        items[0].Label.ShouldStartWith("Pin ");
    }

    [Fact]
    public void BuildPlotModel_ConvertsTimeAxisToPicoseconds()
    {
        var pin = Guid.NewGuid();
        var result = MakeResult(new[] { 0.0, 1e-12, 2e-12 }, (pin, new[] { 0.0, 0.5, 0.2 }));
        var items = TimeTracePlotBuilder.BuildSeriesItems(result, _ => null);

        var model = TimeTracePlotBuilder.BuildPlotModel(result, items);

        var series = model.Series.OfType<LineSeries>().Single();
        series.Points.Count.ShouldBe(3);
        // 2e-12 s == 2 ps on the X axis.
        series.Points[2].X.ShouldBe(2.0, 1e-9);
        series.Points[1].Y.ShouldBe(0.5, 1e-9);
    }

    [Fact]
    public void BuildPlotModel_OmitsHiddenSeries()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var result = MakeResult(
            new[] { 0.0, 1e-12 },
            (a, new[] { 0.0, 0.5 }),
            (b, new[] { 0.0, 0.9 }));
        var items = TimeTracePlotBuilder.BuildSeriesItems(result, _ => null);

        items[0].IsVisible = false; // hide the strongest

        var model = TimeTracePlotBuilder.BuildPlotModel(result, items);

        model.Series.Count.ShouldBe(1);
    }

    [Fact]
    public void BuildPlotModel_AllHidden_ProducesNoSeries()
    {
        var pin = Guid.NewGuid();
        var result = MakeResult(new[] { 0.0, 1e-12 }, (pin, new[] { 0.0, 0.5 }));
        var items = TimeTracePlotBuilder.BuildSeriesItems(result, _ => null);
        items[0].IsVisible = false;

        var model = TimeTracePlotBuilder.BuildPlotModel(result, items);

        model.Series.Count.ShouldBe(0);
    }

    [Fact]
    public void CreateEmptyPlotModel_HasTimeAndPowerAxes()
    {
        var model = TimeTracePlotBuilder.CreateEmptyPlotModel();

        model.Axes.Count.ShouldBe(2);
        model.Series.Count.ShouldBe(0);
        model.Axes.Any(a => a.Title.Contains("Time")).ShouldBeTrue();
        model.Axes.Any(a => a.Title.Contains("Power")).ShouldBeTrue();
    }

    [Fact]
    public void DistinctPalette_AssignedToSeparatePins()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var result = MakeResult(
            new[] { 0.0, 1e-12 },
            (a, new[] { 0.0, 0.9 }),
            (b, new[] { 0.0, 0.5 }));

        var items = TimeTracePlotBuilder.BuildSeriesItems(result, _ => null);

        items[0].Color.ShouldNotBe(items[1].Color);
    }
}
