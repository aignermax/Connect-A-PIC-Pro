using CAP.Avalonia.Commands;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Panels;
using Shouldly;
using UnitTests;
using Xunit;

namespace UnitTests.Commands;

/// <summary>
/// Tests that the Delete key command removes the entire current selection
/// (box selection / multi-selection), respects locked components, and that a
/// single undo restores everything including connections.
/// Regression tests for the field bug where DEL did not delete the whole
/// box-selected set.
/// </summary>
public class BoxSelectionDeleteTests
{
    private static (DesignCanvasViewModel canvas, CommandManager commandManager,
        CanvasInteractionViewModel interaction,
        ComponentViewModel vm1, ComponentViewModel vm2, ComponentViewModel vm3) CreateSetup()
    {
        var canvas = new DesignCanvasViewModel();
        var commandManager = new CommandManager();
        var interaction = new CanvasInteractionViewModel(canvas, commandManager);

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

        // Connection between component 1 and component 2.
        canvas.ConnectPins(vms[0].Component.PhysicalPins[1], vms[1].Component.PhysicalPins[0]);

        return (canvas, commandManager, interaction, vms[0], vms[1], vms[2]);
    }

    /// <summary>Simulates a rubber-band selection over all three components.</summary>
    private static void BoxSelectAll(DesignCanvasViewModel canvas)
    {
        canvas.Selection.SelectInRectangle(canvas.Components, -10, -10, 5000, 5000);
    }

    [Fact]
    public void DeleteSelected_BoxSelectedThree_DeletesAllComponentsAndConnections()
    {
        var (canvas, commandManager, interaction, _, _, _) = CreateSetup();
        BoxSelectAll(canvas);

        interaction.DeleteSelectedCommand.Execute(null);

        canvas.Components.ShouldBeEmpty();
        canvas.Connections.ShouldBeEmpty();
        commandManager.UndoCount.ShouldBe(1); // one batch command → one undo step
    }

    [Fact]
    public void DeleteSelected_SingleBoxSelectedComponent_DeletesIt()
    {
        var (canvas, _, interaction, vm1, _, _) = CreateSetup();

        // Box selection over the first component only. This never sets
        // SelectedComponent — DEL must still delete the selection.
        canvas.Selection.SelectInRectangle(canvas.Components, -10, -10, 300, 300);
        canvas.Selection.SelectedComponents.ShouldBe(new[] { vm1 });

        interaction.DeleteSelectedCommand.Execute(null);

        canvas.Components.ShouldNotContain(vm1);
        canvas.Components.Count.ShouldBe(2);
    }

    [Fact]
    public void DeleteSelected_LockedComponentInSelection_IsNotDeleted()
    {
        var (canvas, _, interaction, _, vm2, _) = CreateSetup();
        vm2.Component.IsLocked = true;
        BoxSelectAll(canvas);

        interaction.DeleteSelectedCommand.Execute(null);

        canvas.Components.ShouldBe(new[] { vm2 });
    }

    [Fact]
    public void DeleteSelected_AllSelectedLocked_DoesNothing()
    {
        var (canvas, commandManager, interaction, vm1, vm2, vm3) = CreateSetup();
        vm1.Component.IsLocked = true;
        vm2.Component.IsLocked = true;
        vm3.Component.IsLocked = true;
        BoxSelectAll(canvas);

        interaction.DeleteSelectedCommand.Execute(null);

        canvas.Components.Count.ShouldBe(3);
        commandManager.UndoCount.ShouldBe(0);
    }

    [Fact]
    public void Undo_AfterBoxSelectionDelete_RestoresComponentsAndConnections()
    {
        var (canvas, commandManager, interaction, _, _, _) = CreateSetup();
        BoxSelectAll(canvas);
        interaction.DeleteSelectedCommand.Execute(null);

        commandManager.Undo();

        canvas.Components.Count.ShouldBe(3);
        canvas.Connections.Count.ShouldBe(1);
    }

    [Fact]
    public void Undo_AfterDeleteWithLockedComponent_DoesNotDuplicateLocked()
    {
        var (canvas, commandManager, interaction, _, vm2, _) = CreateSetup();
        vm2.Component.IsLocked = true;
        BoxSelectAll(canvas);
        interaction.DeleteSelectedCommand.Execute(null);

        commandManager.Undo();

        canvas.Components.Count.ShouldBe(3);
        canvas.Components.Count(c => c.Component == vm2.Component).ShouldBe(1);
    }

    [Fact]
    public void GroupDeleteCommand_WithLockedComponent_UndoDoesNotRestoreDuplicate()
    {
        var (canvas, _, _, vm1, vm2, vm3) = CreateSetup();
        vm2.Component.IsLocked = true;

        // Direct command usage with a locked component in the list: Execute skips
        // it, so Undo must not re-add it a second time.
        var cmd = new GroupDeleteCommand(canvas, new[] { vm1, vm2, vm3 });
        cmd.Execute();

        canvas.Components.Count(c => c.Component == vm2.Component).ShouldBe(1);

        cmd.Undo();

        canvas.Components.Count.ShouldBe(3);
        canvas.Components.Count(c => c.Component == vm2.Component).ShouldBe(1);
    }
}
