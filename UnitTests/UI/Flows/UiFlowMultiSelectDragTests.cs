using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using CAP.Avalonia.Controls;
using CAP.Avalonia.ViewModels.Panels;
using Shouldly;
using Xunit;

namespace UnitTests.UI.Flows;

/// <summary>
/// Field round 4, review finding [0], through the real input pipeline: a multi-selection
/// must survive the click-through that happens when a member is pressed. Ctrl+click grows
/// the selection instead of collapsing it, and dragging one member moves the whole group —
/// both broke when the click sync unconditionally re-selected the clicked component.
/// </summary>
[Trait("Category", "UiFlows")]
// Boots the real MainWindow through the input pipeline — too heavy for local default
// runs (CI covers it, the local runners exclude Category=Slow).
[Trait("Category", "Slow")]
[Collection("LocalizationSingleton")]
public class UiFlowMultiSelectDragTests
{
    private const string PdkName = "Demo PDK";
    private const string ComponentName = "1x2 MMI Splitter"; // 80 × 55 µm

    [AvaloniaFact]
    public void CtrlClickGrowsSelection_dragOnMemberMovesWholeGroup()
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

        // Rubber-band over the two left components, then Ctrl+click the third: the
        // zero-size box release routes through the click pipeline and must ADD, not
        // collapse the set back to one.
        UiInput.DragMouse(win, CanvasPoint(60, 60), CanvasPoint(260, 380));
        vm.Canvas.Selection.SelectedComponents.Count.ShouldBe(2);
        UiInput.ClickAt(win, CanvasPoint(600, 150), RawInputModifiers.Control);
        vm.Canvas.Selection.SelectedComponents.Count.ShouldBe(3,
            "Ctrl+click must accumulate onto the box selection");

        // Drag the first member: pressing a member keeps the set, so ALL three move.
        var before = vm.Canvas.Components.Select(c => (c.X, c.Y)).ToArray();
        UiInput.DragMouse(win, CanvasPoint(150, 150), CanvasPoint(190, 180));
        UiInput.RunJobs();

        vm.Canvas.Selection.SelectedComponents.Count.ShouldBe(3,
            "the multi-selection must survive the drag");
        var deltas = vm.Canvas.Components
            .Select((c, i) => (Dx: c.X - before[i].X, Dy: c.Y - before[i].Y)).ToArray();
        deltas[0].Dx.ShouldBeGreaterThan(20, "the grabbed component must have moved");
        for (int i = 1; i < deltas.Length; i++)
        {
            deltas[i].Dx.ShouldBe(deltas[0].Dx, 0.01, $"component {i} must move with the group");
            deltas[i].Dy.ShouldBe(deltas[0].Dy, 0.01, $"component {i} must move with the group");
        }
    }
}
