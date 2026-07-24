using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using CAP.Avalonia.Controls;
using CAP.Avalonia.ViewModels.Panels;
using Shouldly;
using Xunit;

namespace UnitTests.UI.Flows;

/// <summary>
/// Field bug (round 4 final): box-select everything, then left-click empty canvas —
/// the canvas visually deselected but the hierarchy panel kept the multi-highlight,
/// because the plain-click path cleared the per-component flags without clearing the
/// <c>SelectionManager</c> set the hierarchy mirrors. The click must empty BOTH.
/// </summary>
[Trait("Category", "UiFlows")]
[Collection("LocalizationSingleton")]
public class UiFlowEmptyClickDeselectTests
{
    private const string PdkName = "Demo PDK";
    private const string ComponentName = "1x2 MMI Splitter"; // 80 × 55 µm

    [AvaloniaFact]
    public void BoxSelectAll_thenClickEmptyCanvas_clearsCanvasAndHierarchySelection()
    {
        using var host = new UiFlowTestHost();
        var vm = host.Vm;
        var win = host.Window;

        // Arm placement from the library and place three components.
        var template = vm.LeftPanel.AllTemplates.Single(t => t.PdkSource == PdkName && t.Name == ComponentName);
        var library = host.LibraryListBox;
        library.ScrollIntoView(template);
        UiInput.RunJobs();
        var row = (Avalonia.Controls.ListBoxItem)library.ContainerFromItem(template)!;
        UiInput.Click(win, row, relX: 0.35);
        vm.CanvasInteraction.CurrentMode.ShouldBe(InteractionMode.PlaceComponent);

        var canvasControl = UiInput.Descendants<DesignCanvas>(win).First();
        Point CanvasPoint(double x, double y) =>
            canvasControl.TranslatePoint(
                new Point(x * canvasControl.Zoom + vm.Canvas.PanX, y * canvasControl.Zoom + vm.Canvas.PanY), win)!.Value;

        UiInput.ClickAt(win, CanvasPoint(150, 150));
        UiInput.ClickAt(win, CanvasPoint(150, 300));
        UiInput.ClickAt(win, CanvasPoint(600, 150));
        vm.Canvas.Components.Count.ShouldBe(3);

        UiInput.PressKey(win, Key.S);
        vm.CanvasInteraction.CurrentMode.ShouldBe(InteractionMode.Select);

        // Rubber-band over ALL three components.
        UiInput.DragMouse(win, CanvasPoint(60, 60), CanvasPoint(700, 400));
        vm.Canvas.Selection.SelectedComponents.Count.ShouldBe(3,
            "the box must select all three components");
        vm.LeftPanel.HierarchyPanel.RootNodes.Count(n => n.IsSelected).ShouldBe(3,
            "the hierarchy panel must mirror the box selection");

        // Plain left-click on empty canvas space deselects EVERYTHING —
        // canvas flags, the selection set, and the hierarchy highlight.
        UiInput.ClickAt(win, CanvasPoint(750, 430));

        vm.Canvas.Selection.SelectedComponents.ShouldBeEmpty(
            "an empty-canvas click must clear the multi-selection set");
        vm.Canvas.Components.Count(c => c.IsSelected).ShouldBe(0);
        vm.LeftPanel.HierarchyPanel.RootNodes.Count(n => n.IsSelected).ShouldBe(0,
            "the hierarchy panel must drop the multi-highlight when the canvas deselects");
    }
}
