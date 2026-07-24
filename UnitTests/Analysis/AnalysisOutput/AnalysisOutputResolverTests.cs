using CAP.Avalonia.ViewModels.Analysis.AnalysisOutput;
using CAP.Avalonia.ViewModels.Canvas;
using Shouldly;

namespace UnitTests.Analysis.AnalysisOutput;

/// <summary>
/// Tests for <see cref="AnalysisOutputResolver"/> (#754): the designated coupler wins,
/// an invalid designation is classified (and a deleted one pruned), and without a
/// designation the off-coupler heuristics of #690 are preserved.
/// </summary>
public class AnalysisOutputResolverTests
{
    [Fact]
    public void Resolve_DesignatedOffCoupler_IsValid()
    {
        var canvas = new DesignCanvasViewModel();
        var coupler = AnalysisOutputTestBed.AddCoupler(canvas);
        coupler.LaserConfig!.IsEnabled = false;
        canvas.AnalysisOutput.Designate(coupler.Component.Id);

        var resolution = AnalysisOutputResolver.Resolve(canvas);

        resolution.State.ShouldBe(AnalysisOutputState.DesignatedValid);
        resolution.Output.ShouldBe(coupler);
    }

    [Fact]
    public void Resolve_DesignatedCouplerWithLaserOn_IsReportedNotGuessed()
    {
        var canvas = new DesignCanvasViewModel();
        var coupler = AnalysisOutputTestBed.AddCoupler(canvas);
        canvas.AnalysisOutput.Designate(coupler.Component.Id);

        var resolution = AnalysisOutputResolver.Resolve(canvas);

        resolution.State.ShouldBe(AnalysisOutputState.DesignatedLaserOn);
        resolution.Output.ShouldBe(coupler);
        canvas.AnalysisOutput.CouplerId.ShouldNotBeNull("laser-on keeps the designation, only the run warns");
    }

    [Fact]
    public void Resolve_DesignatedCouplerDeleted_ReportsMissingAndPrunes()
    {
        var canvas = new DesignCanvasViewModel();
        var coupler = AnalysisOutputTestBed.AddCoupler(canvas);
        canvas.AnalysisOutput.Designate(coupler.Component.Id);
        canvas.RemoveComponent(coupler);

        var resolution = AnalysisOutputResolver.Resolve(canvas);

        resolution.State.ShouldBe(AnalysisOutputState.DesignatedMissing);
        canvas.AnalysisOutput.CouplerId.ShouldBeNull("a stale designation must be cleared on access");
    }

    [Fact]
    public void Resolve_NoDesignation_SingleOffCoupler_AutoSelectsIt()
    {
        var canvas = new DesignCanvasViewModel();
        var input = AnalysisOutputTestBed.AddCoupler(canvas);
        var output = AnalysisOutputTestBed.AddCoupler(canvas, x: 100);
        output.LaserConfig!.IsEnabled = false;
        _ = input;

        var resolution = AnalysisOutputResolver.Resolve(canvas);

        resolution.State.ShouldBe(AnalysisOutputState.AutoSingle);
        resolution.Output.ShouldBe(output);
    }

    [Fact]
    public void Resolve_NoDesignation_SeveralOffCouplers_IsAmbiguous()
    {
        var canvas = new DesignCanvasViewModel();
        var a = AnalysisOutputTestBed.AddCoupler(canvas);
        var b = AnalysisOutputTestBed.AddCoupler(canvas, x: 100);
        a.LaserConfig!.IsEnabled = false;
        b.LaserConfig!.IsEnabled = false;

        var resolution = AnalysisOutputResolver.Resolve(canvas);

        resolution.State.ShouldBe(AnalysisOutputState.MultipleCandidates);
        resolution.Output.ShouldBeNull();
        resolution.Candidates.ShouldBe(new[] { a, b }, ignoreOrder: true);
    }

    [Fact]
    public void Resolve_NoDesignation_AllLasersOn_KeepsLegacyClassification()
    {
        var canvas = new DesignCanvasViewModel();
        AnalysisOutputTestBed.AddCoupler(canvas);

        AnalysisOutputResolver.Resolve(canvas).State.ShouldBe(AnalysisOutputState.AllLasersOn);
    }

    [Fact]
    public void Resolve_NoCouplers_IsClassifiedAsSuch()
    {
        var canvas = new DesignCanvasViewModel();
        AnalysisOutputTestBed.AddPlainComponent(canvas);

        AnalysisOutputResolver.Resolve(canvas).State.ShouldBe(AnalysisOutputState.NoCouplers);
    }

    [Fact]
    public void CollectLightPinIds_ReturnsBothFlowDirections()
    {
        var canvas = new DesignCanvasViewModel();
        var coupler = AnalysisOutputTestBed.AddCoupler(canvas);

        var pinIds = AnalysisOutputResolver.CollectLightPinIds(coupler);

        // Two light pins × two flow directions.
        pinIds.Count.ShouldBe(4);
        foreach (var pin in coupler.Component.PhysicalPins)
        {
            pinIds.ShouldContain(pin.LogicalPin!.IDInFlow);
            pinIds.ShouldContain(pin.LogicalPin!.IDOutFlow);
        }
    }
}
