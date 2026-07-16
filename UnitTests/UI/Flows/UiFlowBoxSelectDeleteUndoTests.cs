using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using CAP.Avalonia.Controls;
using CAP.Avalonia.ViewModels.Panels;
using Shouldly;
using Xunit;

namespace UnitTests.UI.Flows;

/// <summary>
/// User story: place three components by clicking the canvas, rubber-band-select two of them
/// with a mouse drag, press Delete, press Ctrl+Z — all through the real MainWindow input
/// pipeline. The hierarchy panel must mirror the selection and the undo must restore both.
/// </summary>
[Trait("Category", "UiFlows")]
public class UiFlowBoxSelectDeleteUndoTests
{
    private const string PdkName = "Demo PDK";
    private const string ComponentName = "1x2 MMI Splitter"; // 80 × 55 µm

    [AvaloniaFact]
    public void BoxSelectTwoOfThree_deleteWithKey_undoRestoresBoth()
    {
        using var host = new UiFlowTestHost();
        var vm = host.Vm;
        var win = host.Window;

        // Select the component in the library like a user: click its row → placement mode.
        var template = vm.LeftPanel.AllTemplates.Single(t => t.PdkSource == PdkName && t.Name == ComponentName);
        var library = host.LibraryListBox;
        library.ScrollIntoView(template);
        UiInput.RunJobs();
        var row = (ListBoxItem)library.ContainerFromItem(template)!;
        // Click the left part of the row — the right edge hosts the hover ✏/✕ actions.
        UiInput.Click(win, row, relX: 0.35);
        vm.CanvasInteraction.CurrentMode.ShouldBe(InteractionMode.PlaceComponent,
            "clicking a library row must arm component placement");

        var canvasControl = UiInput.Descendants<DesignCanvas>(win).First();
        // Canvas µm → window point: default zoom 1, pan 0, grid snap off.
        Point CanvasPoint(double x, double y) =>
            canvasControl.TranslatePoint(
                new Point(x * canvasControl.Zoom + vm.Canvas.PanX, y * canvasControl.Zoom + vm.Canvas.PanY), win)!.Value;

        // Placement centers the 80×55 template on the click point.
        UiInput.ClickAt(win, CanvasPoint(150, 150));
        UiInput.ClickAt(win, CanvasPoint(150, 300));
        UiInput.ClickAt(win, CanvasPoint(600, 150));
        vm.Canvas.Components.Count.ShouldBe(3,
            $"three canvas clicks must place three components (status: {vm.StatusText})");
        vm.LeftPanel.HierarchyPanel.RootNodes.Count.ShouldBe(3);

        // S = select mode (the canvas keyboard handler owns this shortcut).
        UiInput.PressKey(win, Key.S);
        vm.CanvasInteraction.CurrentMode.ShouldBe(InteractionMode.Select);

        // Rubber-band over the two left components (both fully inside 60..260 × 60..380).
        UiInput.DragMouse(win, CanvasPoint(60, 60), CanvasPoint(260, 380));
        vm.Canvas.Selection.SelectedComponents.Count.ShouldBe(2,
            "the box must select exactly the two components it covers");
        vm.LeftPanel.HierarchyPanel.RootNodes.Count(n => n.IsSelected).ShouldBe(2,
            "the hierarchy panel must mirror the box selection");

        UiInput.PressKey(win, Key.Delete);
        vm.Canvas.Components.Count.ShouldBe(1, "Delete must remove both selected components");
        vm.LeftPanel.HierarchyPanel.RootNodes.Count.ShouldBe(1);

        UiInput.PressKey(win, Key.Z, RawInputModifiers.Control);
        vm.Canvas.Components.Count.ShouldBe(3, "a single Ctrl+Z must restore both deleted components");
        vm.LeftPanel.HierarchyPanel.RootNodes.Count.ShouldBe(3);
    }
}
