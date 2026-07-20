using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Analysis;
using CAP.Avalonia.ViewModels.Analysis.EyeDiagram;
using CAP.Avalonia.ViewModels.Canvas;
using Shouldly;

namespace UnitTests.Analysis.AnalysisOutput;

/// <summary>
/// Verifies that the Eye/BER and Transient analyses share ONE designation source
/// (#754) and never guess silently: an invalid designation (deleted coupler, laser
/// switched back on) yields the same clear warning in BOTH tabs, and an ambiguous
/// design activates the canvas picker on Run.
/// </summary>
public class AnalysisOutputAnalysisIntegrationTests
{
    private static (DesignCanvasViewModel Canvas, EyeDiagramViewModel Eye, TimeDomainViewModel Transient) CreateTabs()
    {
        var canvas = new DesignCanvasViewModel();
        var eye = new EyeDiagramViewModel();
        var transient = new TimeDomainViewModel();
        eye.Configure(canvas);
        transient.Configure(canvas);
        return (canvas, eye, transient);
    }

    [Fact]
    public async Task DesignatedLaserOn_BothTabsWarnIdentically_AndDoNotRun()
    {
        var (canvas, eye, transient) = CreateTabs();
        var coupler = AnalysisOutputTestBed.AddCoupler(canvas);
        canvas.AnalysisOutput.Designate(coupler.Component.Id);

        await eye.RunEyeAnalysisCommand.ExecuteAsync(null);
        await transient.RunTransientCommand.ExecuteAsync(null);

        var expected = string.Format(
            LocalizationService.Instance.Translate("Analysis.Output.DesignatedLaserOn"), coupler.Name);
        eye.StatusText.ShouldBe(expected);
        transient.StatusText.ShouldBe(expected);
        eye.HasResult.ShouldBeFalse();
        transient.HasResult.ShouldBeFalse();
    }

    [Fact]
    public async Task DesignatedCouplerDeleted_BothTabsWarn_AndRequestThePicker()
    {
        var (canvas, eye, transient) = CreateTabs();
        var input = AnalysisOutputTestBed.AddCoupler(canvas);
        var output = AnalysisOutputTestBed.AddCoupler(canvas, x: 100);
        _ = input;
        canvas.AnalysisOutput.Designate(output.Component.Id);
        canvas.RemoveComponent(output);
        int pickerRequests = 0;
        eye.RequestOutputPicker = () => pickerRequests++;
        transient.RequestOutputPicker = () => pickerRequests++;

        await eye.RunEyeAnalysisCommand.ExecuteAsync(null);
        var expected = LocalizationService.Instance.Translate("Analysis.Output.DesignatedMissing");
        eye.StatusText.ShouldBe(expected);
        pickerRequests.ShouldBe(1);

        // The first access pruned the designation; re-designate to prove the transient
        // tab reads the SAME source of truth and reacts identically.
        canvas.AnalysisOutput.Designate(Guid.NewGuid());
        await transient.RunTransientCommand.ExecuteAsync(null);
        transient.StatusText.ShouldBe(expected);
        pickerRequests.ShouldBe(2);
    }

    [Fact]
    public async Task MultipleCandidatesWithoutDesignation_EyeRun_RunsWithoutForcingThePicker()
    {
        // Field wish (round 4 final): without an explicit designation every coupler
        // with its laser off IS an output (pre-#757 behaviour) — the eyedropper only
        // RESTRICTS. The run must not push the canvas into picker mode.
        var (canvas, eye, _) = CreateTabs();
        AnalysisOutputTestBed.AddCoupler(canvas);                             // input, laser on
        AnalysisOutputTestBed.AddCoupler(canvas, x: 100).LaserConfig!.IsEnabled = false;
        AnalysisOutputTestBed.AddCoupler(canvas, x: 200).LaserConfig!.IsEnabled = false;
        bool pickerRequested = false;
        eye.RequestOutputPicker = () => pickerRequested = true;

        await eye.RunEyeAnalysisCommand.ExecuteAsync(null);

        pickerRequested.ShouldBeFalse("without a designation all off couplers are outputs — no forced picker");
    }

    [Fact]
    public async Task MultipleCandidatesWithoutDesignation_TransientRun_RunsWithoutForcingThePicker()
    {
        var (canvas, _, transient) = CreateTabs();
        AnalysisOutputTestBed.AddCoupler(canvas);                             // input, laser on
        AnalysisOutputTestBed.AddCoupler(canvas, x: 100).LaserConfig!.IsEnabled = false;
        AnalysisOutputTestBed.AddCoupler(canvas, x: 200).LaserConfig!.IsEnabled = false;
        bool pickerRequested = false;
        transient.RequestOutputPicker = () => pickerRequested = true;

        await transient.RunTransientCommand.ExecuteAsync(null);

        pickerRequested.ShouldBeFalse("without a designation all off couplers are outputs — no forced picker");
    }

    [Fact]
    public async Task ValidDesignation_DoesNotRequestThePicker()
    {
        var (canvas, eye, _) = CreateTabs();
        AnalysisOutputTestBed.AddCoupler(canvas);                             // input, laser on
        var output = AnalysisOutputTestBed.AddCoupler(canvas, x: 100);
        output.LaserConfig!.IsEnabled = false;
        canvas.AnalysisOutput.Designate(output.Component.Id);
        bool pickerRequested = false;
        eye.RequestOutputPicker = () => pickerRequested = true;

        await eye.RunEyeAnalysisCommand.ExecuteAsync(null);

        pickerRequested.ShouldBeFalse();
    }
}
