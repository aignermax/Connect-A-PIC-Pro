using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Analysis.AnalysisOutput;
using CAP.Avalonia.ViewModels.Canvas;
using Shouldly;

namespace UnitTests.Analysis.AnalysisOutput;

/// <summary>
/// Tests for the analysis-dock output header (#754): it mirrors the design-wide
/// designation, clears it, starts the picker, and prunes a designation whose
/// component was deleted.
/// </summary>
public class AnalysisOutputPanelViewModelTests
{
    private static (DesignCanvasViewModel Canvas, AnalysisOutputPanelViewModel Panel) CreatePanel()
    {
        var canvas = new DesignCanvasViewModel();
        var panel = new AnalysisOutputPanelViewModel();
        panel.Configure(canvas);
        return (canvas, panel);
    }

    [Fact]
    public void WithoutDesignation_ShowsAutomaticPlaceholder()
    {
        var (_, panel) = CreatePanel();

        panel.HasOutput.ShouldBeFalse();
        panel.OutputDisplayName.ShouldBe(LocalizationService.Instance.Translate("Analysis.Output.None"));
    }

    [Fact]
    public void WithoutDesignation_SingleOffLaserCoupler_ShowsItsNameAutomatically()
    {
        // Field feedback: a bare "(automatic)" tells you nothing while the tab
        // is open — the header must name the output the analyses would use.
        var (canvas, panel) = CreatePanel();
        var coupler = AnalysisOutputTestBed.AddCoupler(canvas);
        coupler.LaserConfig!.IsEnabled = false;

        panel.HasOutput.ShouldBeFalse();
        panel.OutputDisplayName.ShouldBe(string.Format(
            LocalizationService.Instance.Translate("Analysis.Output.AutoNamed"), coupler.Name));
    }

    [Fact]
    public void LaserToggle_RefreshesTheAutomaticName()
    {
        var (canvas, panel) = CreatePanel();
        var coupler = AnalysisOutputTestBed.AddCoupler(canvas);
        coupler.LaserConfig!.IsEnabled = false;
        panel.OutputDisplayName.ShouldContain(coupler.Name);

        coupler.LaserConfig.IsEnabled = true;

        panel.OutputDisplayName.ShouldBe(
            LocalizationService.Instance.Translate("Analysis.Output.AutoAllLasersOn"));
    }

    [Fact]
    public void Designation_ShowsCouplerName()
    {
        var (canvas, panel) = CreatePanel();
        var coupler = AnalysisOutputTestBed.AddCoupler(canvas);

        canvas.AnalysisOutput.Designate(coupler.Component.Id);

        panel.HasOutput.ShouldBeTrue();
        panel.OutputDisplayName.ShouldBe(coupler.Name);
    }

    [Fact]
    public void ClearCommand_RemovesTheDesignation()
    {
        var (canvas, panel) = CreatePanel();
        var coupler = AnalysisOutputTestBed.AddCoupler(canvas);
        canvas.AnalysisOutput.Designate(coupler.Component.Id);

        panel.ClearCommand.Execute(null);

        canvas.AnalysisOutput.CouplerId.ShouldBeNull();
        panel.HasOutput.ShouldBeFalse();
    }

    [Fact]
    public void DeletingTheDesignatedCoupler_PrunesDesignationAndDisplay()
    {
        var (canvas, panel) = CreatePanel();
        var coupler = AnalysisOutputTestBed.AddCoupler(canvas);
        canvas.AnalysisOutput.Designate(coupler.Component.Id);

        canvas.RemoveComponent(coupler);

        canvas.AnalysisOutput.CouplerId.ShouldBeNull();
        panel.HasOutput.ShouldBeFalse();
        panel.OutputDisplayName.ShouldBe(LocalizationService.Instance.Translate("Analysis.Output.None"));
    }

    [Fact]
    public void PickCommand_InvokesTheWiredCallback()
    {
        var (_, panel) = CreatePanel();
        bool invoked = false;
        panel.PickRequested = () => invoked = true;

        panel.PickCommand.Execute(null);

        invoked.ShouldBeTrue();
    }

    [Fact]
    public void Reconfigure_FollowsTheNewCanvas()
    {
        var (_, panel) = CreatePanel();
        var otherCanvas = new DesignCanvasViewModel();
        var coupler = AnalysisOutputTestBed.AddCoupler(otherCanvas);
        panel.Configure(otherCanvas);

        otherCanvas.AnalysisOutput.Designate(coupler.Component.Id);

        panel.OutputDisplayName.ShouldBe(coupler.Name);
    }
}
