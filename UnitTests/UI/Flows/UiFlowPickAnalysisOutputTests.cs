using Avalonia;
using Avalonia.Headless.XUnit;
using CAP.Avalonia.Controls;
using CAP.Avalonia.ViewModels.Panels;
using Shouldly;
using UnitTests.Analysis.AnalysisOutput;
using Xunit;

namespace UnitTests.UI.Flows;

/// <summary>
/// User story for field-round-4 finding A: press "Pick on canvas" (the eyedropper of the
/// analysis dock, #754/#757), then CLICK a highlighted grating coupler — the click must
/// designate that coupler as the analysis output and end the picker mode. Before the fix
/// no gesture recognizer handled left-clicks in <see cref="InteractionMode.PickAnalysisOutput"/>,
/// so the click silently did nothing.
/// </summary>
[Trait("Category", "UiFlows")]
// Boots the real MainWindow through the input pipeline — too heavy for local default
// runs (CI covers it, the local runners exclude Category=Slow).
[Trait("Category", "Slow")]
[Collection("LocalizationSingleton")]
public class UiFlowPickAnalysisOutputTests
{
    [AvaloniaFact]
    public void PickerClick_onCoupler_designatesItAndEndsThePicker()
    {
        using var host = new UiFlowTestHost();
        var vm = host.Vm;
        var win = host.Window;

        // Two couplers (20×10 µm each): the left stays the input, the right is clicked.
        var input = AnalysisOutputTestBed.AddCoupler(vm.Canvas, x: 100, y: 100);
        var target = AnalysisOutputTestBed.AddCoupler(vm.Canvas, x: 300, y: 100);
        UiInput.RunJobs();
        target.LaserConfig!.IsEnabled.ShouldBeTrue("couplers start with the laser on");

        // The dock header's eyedropper button arms the picker on the canvas.
        vm.BottomPanel.Analysis.Output.PickCommand.Execute(null);
        UiInput.RunJobs();
        vm.CanvasInteraction.CurrentMode.ShouldBe(InteractionMode.PickAnalysisOutput);

        var canvasControl = UiInput.Descendants<DesignCanvas>(win).First();
        Point CanvasPoint(double x, double y) =>
            canvasControl.TranslatePoint(
                new Point(x * canvasControl.Zoom + vm.Canvas.PanX, y * canvasControl.Zoom + vm.Canvas.PanY),
                win)!.Value;

        // Click the centre of the right coupler through the real input pipeline.
        UiInput.ClickAt(win, CanvasPoint(310, 105));

        vm.Canvas.AnalysisOutput.CouplerId.ShouldBe(target.Component.Id,
            $"the picker click must designate the clicked coupler (status: {vm.StatusText})");
        target.IsLaserOff.ShouldBeTrue("designating an emitting coupler switches its laser off");
        input.IsLaserOff.ShouldBeFalse("the input coupler must stay untouched");
        vm.CanvasInteraction.CurrentMode.ShouldBe(InteractionMode.Select,
            "a successful pick returns the canvas to Select mode");
    }

    [AvaloniaFact]
    public void PickerClick_onEmptyCanvas_keepsThePickerArmed()
    {
        using var host = new UiFlowTestHost();
        var vm = host.Vm;
        var win = host.Window;

        AnalysisOutputTestBed.AddCoupler(vm.Canvas, x: 100, y: 100);
        UiInput.RunJobs();

        vm.BottomPanel.Analysis.Output.PickCommand.Execute(null);
        UiInput.RunJobs();

        var canvasControl = UiInput.Descendants<DesignCanvas>(win).First();
        var emptySpot = canvasControl.TranslatePoint(
            new Point(600 * canvasControl.Zoom + vm.Canvas.PanX, 300 * canvasControl.Zoom + vm.Canvas.PanY),
            win)!.Value;
        UiInput.ClickAt(win, emptySpot);

        vm.Canvas.AnalysisOutput.CouplerId.ShouldBeNull("clicking empty space designates nothing");
        vm.CanvasInteraction.CurrentMode.ShouldBe(InteractionMode.PickAnalysisOutput,
            "a miss keeps the picker armed with a status hint");
    }
}
