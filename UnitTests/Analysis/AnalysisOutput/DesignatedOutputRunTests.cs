using CAP.Avalonia.ViewModels.Analysis;
using CAP.Avalonia.ViewModels.Analysis.EyeDiagram;
using CAP.Avalonia.ViewModels.Canvas;
using Shouldly;

namespace UnitTests.Analysis.AnalysisOutput;

/// <summary>
/// Regression tests for field-round-4 findings B/C: a CORRECT setup — input coupler with
/// its laser ON, connected to a designated output coupler with its laser OFF — must run
/// the Eye/BER and Transient analyses and produce a result at the designated coupler.
/// Before the multi-hop fix the transient pipeline only carried one-hop transfers, so no
/// light ever "arrived" at the output coupler and the gate refused with a misleading
/// "no light arrives at the coupler(s) with the laser switched off".
/// </summary>
public class DesignatedOutputRunTests
{
    /// <summary>
    /// Canvas with two couplers joined by a real waveguide connection:
    /// input (laser on) east pin → output (laser off, designated) west pin.
    /// </summary>
    private static (DesignCanvasViewModel Canvas, ComponentViewModel Input, ComponentViewModel Output) CreateConnectedPair()
    {
        var canvas = new DesignCanvasViewModel();
        var input = AnalysisOutputTestBed.AddCoupler(canvas);
        var output = AnalysisOutputTestBed.AddCoupler(canvas, x: 100);
        output.LaserConfig!.IsEnabled = false;
        canvas.AnalysisOutput.Designate(output.Component.Id);

        var inputEast = input.Component.PhysicalPins.First(p => p.Name == "east0");
        var outputWest = output.Component.PhysicalPins.First(p => p.Name == "west0");
        canvas.ConnectPins(inputEast, outputWest).ShouldNotBeNull();
        return (canvas, input, output);
    }

    [Fact]
    public async Task EyeAnalysis_InputOnOutputOffDesignated_RunsAndProducesAResult()
    {
        var (canvas, _, _) = CreateConnectedPair();
        var eye = new EyeDiagramViewModel();
        eye.Configure(canvas);
        bool pickerRequested = false;
        eye.RequestOutputPicker = () => pickerRequested = true;

        await eye.RunEyeAnalysisCommand.ExecuteAsync(null);

        eye.HasResult.ShouldBeTrue(
            $"light DOES arrive at the connected output coupler — status was: '{eye.StatusText}'");
        eye.StatusText.ShouldBe("Done");
        eye.MetricsText.ShouldNotBeNullOrWhiteSpace();
        pickerRequested.ShouldBeFalse();
    }

    [Fact]
    public async Task EyeAnalysis_Result_PlotsANonEmptyHistogram()
    {
        var (canvas, _, _) = CreateConnectedPair();
        var eye = new EyeDiagramViewModel();
        eye.Configure(canvas);

        await eye.RunEyeAnalysisCommand.ExecuteAsync(null);

        eye.HasResult.ShouldBeTrue($"status was: '{eye.StatusText}'");
        // The persistence display must actually contain data (finding C: analysis ran
        // but the diagram stayed empty).
        eye.PlotModel.Series.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task TransientAnalysis_InputOnOutputOffDesignated_ShowsTheDesignatedTraces()
    {
        var (canvas, _, output) = CreateConnectedPair();
        var transient = new TimeDomainViewModel();
        transient.Configure(canvas);

        await transient.RunTransientCommand.ExecuteAsync(null);

        transient.HasResult.ShouldBeTrue(
            $"light DOES arrive at the connected output coupler — status was: '{transient.StatusText}'");
        transient.Series.ShouldNotBeEmpty();
        // Every displayed trace belongs to the designated coupler.
        var designatedPinIds = CAP.Avalonia.ViewModels.Analysis.AnalysisOutput
            .AnalysisOutputResolver.CollectLightPinIds(output);
        transient.Series.ShouldAllBe(s => designatedPinIds.Contains(s.PinId));
    }
}
