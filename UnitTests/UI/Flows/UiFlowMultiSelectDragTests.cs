using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using CAP.Avalonia.Controls;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Components.Connections;
using CAP_Core.Routing;
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

    /// <summary>
    /// Through the real input pipeline: dragging BOTH connected components together must keep
    /// the waveguide glued to the pins and preserve the connection's bend radius. Before the
    /// fix the route stayed pinned to its old grid spot during the drag and was re-routed from
    /// scratch on drop, discarding the manual radius.
    /// </summary>
    [AvaloniaFact]
    public void DraggingBothConnectedComponents_KeepsWaveguideOnPinsAndBendRadius()
    {
        using var host = new UiFlowTestHost();
        var vm = host.Vm;
        var win = host.Window;

        var template = vm.LeftPanel.AllTemplates.Single(t => t.PdkSource == PdkName && t.Name == ComponentName);
        var library = host.LibraryListBox;
        library.ScrollIntoView(template);
        UiInput.RunJobs();
        var libRow = (Avalonia.Controls.ListBoxItem)library.ContainerFromItem(template)!;
        UiInput.Click(win, libRow, relX: 0.35);

        var canvasControl = UiInput.Descendants<DesignCanvas>(win).First();
        Point CanvasPoint(double x, double y) =>
            canvasControl.TranslatePoint(
                new Point(x * canvasControl.Zoom + vm.Canvas.PanX, y * canvasControl.Zoom + vm.Canvas.PanY), win)!.Value;

        UiInput.ClickAt(win, CanvasPoint(150, 150));
        UiInput.ClickAt(win, CanvasPoint(600, 150));
        vm.Canvas.Components.Count.ShouldBe(2);

        // Connect a right-facing pin of the left component to a left-facing pin of the right
        // one, with a straight cached route so the geometry is deterministic, then give the
        // connection a distinctive bend radius the way a user would.
        var left = vm.Canvas.Components[0].Component;
        var right = vm.Canvas.Components[1].Component;
        var startPin = left.PhysicalPins.OrderByDescending(p => p.GetAbsolutePosition().Item1).First();
        var endPin = right.PhysicalPins.OrderBy(p => p.GetAbsolutePosition().Item1).First();
        var (sx, sy) = startPin.GetAbsolutePosition();
        var (ex, ey) = endPin.GetAbsolutePosition();
        var route = new RoutedPath();
        route.Segments.Add(new StraightSegment(sx, sy, ex, ey, 0));
        var connVm = vm.Canvas.ConnectPinsWithCachedRoute(startPin, endPin, route);
        connVm.ShouldNotBeNull();
        connVm.Connection.Type = WaveguideType.Bend;
        connVm.Connection.BendRadiusMicrometers = 35.0;

        UiInput.PressKey(win, Key.S);
        UiInput.DragMouse(win, CanvasPoint(40, 40), CanvasPoint(720, 260));
        vm.Canvas.Selection.SelectedComponents.Count.ShouldBe(2, "box select must grab both components");

        UiInput.DragMouse(win, CanvasPoint(150, 150), CanvasPoint(210, 190));
        UiInput.RunJobs();

        // The waveguide's endpoints must still sit on the (moved) pins — the route followed the
        // joint move instead of being left behind — and the manual radius must survive.
        var path = connVm.Connection.RoutedPath!;
        var (newSx, newSy) = startPin.GetAbsolutePosition();
        var (newEx, newEy) = endPin.GetAbsolutePosition();
        path.Segments[0].StartPoint.X.ShouldBe(newSx, 1.0, "waveguide start must stay on the moved start pin");
        path.Segments[0].StartPoint.Y.ShouldBe(newSy, 1.0);
        path.Segments[^1].EndPoint.X.ShouldBe(newEx, 1.0, "waveguide end must stay on the moved end pin");
        path.Segments[^1].EndPoint.Y.ShouldBe(newEy, 1.0);
        connVm.Connection.BendRadiusMicrometers.ShouldBe(35.0, "the manual bend radius must survive the joint move");
    }
}
