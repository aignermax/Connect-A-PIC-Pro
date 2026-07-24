using CAP.Avalonia.ViewModels.Analysis;
using CAP.Avalonia.ViewModels.Analysis.AnalysisOutput;
using CAP.Avalonia.ViewModels.Analysis.EyeDiagram;
using CAP.Avalonia.ViewModels.Canvas;
using Shouldly;

namespace UnitTests.Analysis.AnalysisOutput;

/// <summary>
/// Field wish (round 4 final batch): when NO analysis output is explicitly designated,
/// every coupler with its laser switched off automatically counts as an output (the
/// pre-#757 behaviour) — the eyedropper only RESTRICTS the analysis to one coupler.
/// A design with one laser on and one off must therefore run both analyses on the off
/// coupler without any designation and without pushing the user into picker mode.
/// </summary>
public class AutoDesignationRunTests
{
    /// <summary>
    /// Canvas with two couplers joined by a real waveguide connection: input (laser on)
    /// east pin → output (laser off) west pin. NO designation is made.
    /// </summary>
    private static (DesignCanvasViewModel Canvas, ComponentViewModel Output) CreateConnectedPair()
    {
        var canvas = new DesignCanvasViewModel();
        var input = AnalysisOutputTestBed.AddCoupler(canvas);
        var output = AnalysisOutputTestBed.AddCoupler(canvas, x: 100);
        output.LaserConfig!.IsEnabled = false;

        var inputEast = input.Component.PhysicalPins.First(p => p.Name == "east0");
        var outputWest = output.Component.PhysicalPins.First(p => p.Name == "west0");
        canvas.ConnectPins(inputEast, outputWest).ShouldNotBeNull();
        return (canvas, output);
    }

    [Fact]
    public async Task EyeAnalysis_NoDesignation_OneLaserOnOneOff_RunsOnTheOffCoupler()
    {
        var (canvas, _) = CreateConnectedPair();
        var eye = new EyeDiagramViewModel();
        eye.Configure(canvas);
        bool pickerRequested = false;
        eye.RequestOutputPicker = () => pickerRequested = true;

        await eye.RunEyeAnalysisCommand.ExecuteAsync(null);

        eye.HasResult.ShouldBeTrue(
            $"the off coupler is automatically the output — status was: '{eye.StatusText}'");
        eye.StatusText.ShouldBe("Done");
        pickerRequested.ShouldBeFalse();
    }

    [Fact]
    public async Task TransientAnalysis_NoDesignation_OneLaserOnOneOff_ProducesTracesAtTheOffCoupler()
    {
        var (canvas, output) = CreateConnectedPair();
        var transient = new TimeDomainViewModel();
        transient.Configure(canvas);
        bool pickerRequested = false;
        transient.RequestOutputPicker = () => pickerRequested = true;

        await transient.RunTransientCommand.ExecuteAsync(null);

        transient.HasResult.ShouldBeTrue(
            $"the off coupler is automatically the output — status was: '{transient.StatusText}'");
        var outputPinIds = AnalysisOutputResolver.CollectLightPinIds(output);
        transient.Series.ShouldContain(s => outputPinIds.Contains(s.PinId),
            "light must arrive at the automatic output coupler");
        pickerRequested.ShouldBeFalse();
    }
}
