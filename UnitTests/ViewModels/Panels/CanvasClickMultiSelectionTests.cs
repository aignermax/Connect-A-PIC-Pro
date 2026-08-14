using CAP.Avalonia.Commands;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Panels;
using Shouldly;
using UnitTests;
using Xunit;

namespace UnitTests.ViewModels.Panels;

/// <summary>
/// Field round 4, review finding [0]: the click→SelectionManager sync in
/// <see cref="CanvasInteractionViewModel"/> must NOT collapse an existing
/// multi-selection when the clicked component is already part of it — group drag
/// (drag recognizer clicks through before checking the set size) and Ctrl+click
/// accumulation (the zero-size box release routes through the click pipeline)
/// both depend on the set surviving. An empty-canvas click still clears the set
/// (the 66fd2c5d hierarchy-deselect fix must not regress).
/// </summary>
public class CanvasClickMultiSelectionTests
{
    private static (DesignCanvasViewModel canvas, CanvasInteractionViewModel interaction,
        ComponentViewModel vm1, ComponentViewModel vm2, ComponentViewModel vm3) CreateSetup()
    {
        var canvas = new DesignCanvasViewModel();
        var interaction = new CanvasInteractionViewModel(canvas, new CommandManager());

        var vms = new ComponentViewModel[3];
        for (int i = 0; i < 3; i++)
        {
            var comp = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
            comp.PhysicalX = i * 1000;
            comp.PhysicalY = 100;
            comp.WidthMicrometers = 50;
            comp.HeightMicrometers = 50;
            vms[i] = canvas.AddComponent(comp, $"Waveguide{i + 1}");
        }

        interaction.CurrentMode = InteractionMode.Select;
        return (canvas, interaction, vms[0], vms[1], vms[2]);
    }

    /// <summary>Clicks the centre of the given component through the click pipeline.</summary>
    private static void ClickOn(CanvasInteractionViewModel interaction, ComponentViewModel vm) =>
        interaction.CanvasClicked(vm.X + vm.Width / 2, vm.Y + vm.Height / 2);

    [Fact]
    public void CanvasClicked_onMemberOfMultiSelection_keepsTheWholeSet()
    {
        var (canvas, interaction, vm1, vm2, vm3) = CreateSetup();
        canvas.Selection.SelectInRectangle(canvas.Components, -10, -10, 5000, 5000);
        canvas.Selection.SelectedComponents.Count.ShouldBe(3);

        // The drag recognizer clicks through BEFORE it decides between group move and
        // single move — the set must survive so all three components are dragged.
        ClickOn(interaction, vm2);

        canvas.Selection.SelectedComponents.ShouldBe(new[] { vm1, vm2, vm3 }, ignoreOrder: true);
        interaction.SelectedComponent.ShouldBe(vm2, "the clicked member becomes the primary");
        canvas.Components.Count(c => c.IsSelected).ShouldBe(3,
            "every member must stay visually selected after the click-through");
    }

    [Fact]
    public void CanvasClicked_afterCtrlClickAccumulation_keepsTheGrownSet()
    {
        var (canvas, interaction, vm1, vm2, _) = CreateSetup();
        canvas.Selection.SelectSingle(vm1);

        // Ctrl+click: the drag recognizer adds to the set on press, then the zero-size
        // box release routes through the regular click pipeline — which must not
        // collapse the freshly grown set back to the clicked component.
        canvas.Selection.AddToSelection(vm2);
        ClickOn(interaction, vm2);

        canvas.Selection.SelectedComponents.ShouldBe(new[] { vm1, vm2 }, ignoreOrder: true);
    }

    [Fact]
    public void CanvasClicked_onComponentOutsideTheSelection_selectsItAlone()
    {
        var (canvas, interaction, vm1, vm2, vm3) = CreateSetup();
        canvas.Selection.SelectInRectangle(canvas.Components, -10, -10, 1200, 5000); // vm1 + vm2
        canvas.Selection.SelectedComponents.ShouldBe(new[] { vm1, vm2 }, ignoreOrder: true);

        ClickOn(interaction, vm3);

        canvas.Selection.SelectedComponents.ShouldBe(new[] { vm3 });
        vm1.IsSelected.ShouldBeFalse();
        vm2.IsSelected.ShouldBeFalse();
    }

    [Fact]
    public void CanvasClicked_onEmptyCanvas_stillClearsTheMultiSelectionSet()
    {
        var (canvas, interaction, _, _, _) = CreateSetup();
        canvas.Selection.SelectInRectangle(canvas.Components, -10, -10, 5000, 5000);
        canvas.Selection.SelectedComponents.Count.ShouldBe(3);

        interaction.CanvasClicked(4000, 4000); // empty area

        canvas.Selection.SelectedComponents.ShouldBeEmpty(
            "empty-canvas click must clear the set the hierarchy panel mirrors (66fd2c5d)");
        canvas.Components.Count(c => c.IsSelected).ShouldBe(0);
    }

    [Fact]
    public async Task CanvasClicked_onBatchSelectedConnection_keepsTheClickedConnectionSelected()
    {
        // #862 review finding 2: SelectAt marked the clicked connection selected, then the
        // sync's ClearSelection() also emptied the connection batch — deselecting the very
        // connection that was just clicked.
        var canvas = new DesignCanvasViewModel();
        var interaction = new CanvasInteractionViewModel(canvas, new CommandManager());
        interaction.CurrentMode = InteractionMode.Select;

        var connections = new List<WaveguideConnectionViewModel>();
        for (int i = 0; i < 2; i++)
        {
            var start = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
            start.WidthMicrometers = 250;
            start.HeightMicrometers = 250;
            start.PhysicalX = 0;
            start.PhysicalY = i * 600;
            var end = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
            end.WidthMicrometers = 250;
            end.HeightMicrometers = 250;
            end.PhysicalX = 400;
            end.PhysicalY = i * 600 + 300;
            canvas.AddComponent(start);
            canvas.AddComponent(end);
            var connVm = await canvas.ConnectPinsAsync(
                start.PhysicalPins.First(p => p.Name == "out"),
                end.PhysicalPins.First(p => p.Name == "in"));
            connections.Add(connVm!);
        }

        foreach (var conn in connections)
        {
            conn.IsSelected = true;
            canvas.Selection.SelectedConnections.Add(conn);
        }

        // Click a routed-path point of the first connection that lies outside every component.
        var target = connections[0];
        var clickPoint = target.Connection.RoutedPath!.Segments
            .Select(s => ((s.StartPoint.X + s.EndPoint.X) / 2, (s.StartPoint.Y + s.EndPoint.Y) / 2))
            .First(p => !canvas.Components.Any(c =>
                p.Item1 >= c.X && p.Item1 <= c.X + c.Width &&
                p.Item2 >= c.Y && p.Item2 <= c.Y + c.Height));
        interaction.CanvasClicked(clickPoint.Item1, clickPoint.Item2);

        interaction.SelectedWaveguideConnection.ShouldBe(target,
            "the clicked batch member becomes the single selection");
        target.IsSelected.ShouldBeTrue("clicking a batch member must not deselect it");
        canvas.Selection.SelectedConnections.ShouldBeEmpty(
            "a plain click dissolves the batch down to the clicked connection");
    }

    [Fact]
    public void SelectComponentAt_rightClickOutsideSelection_collapsesViaTheSharedSync()
    {
        // Right-click shares SelectAt's sync (finding [8]: one sync point, no copies).
        var (canvas, interaction, vm1, vm2, vm3) = CreateSetup();
        canvas.Selection.SelectInRectangle(canvas.Components, -10, -10, 1200, 5000); // vm1 + vm2

        interaction.SelectComponentAt(vm3.X + 10, vm3.Y + 10);

        canvas.Selection.SelectedComponents.ShouldBe(new[] { vm3 });
        vm1.IsSelected.ShouldBeFalse();
        vm2.IsSelected.ShouldBeFalse();
    }
}
