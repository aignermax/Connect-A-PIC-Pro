using CAP.Avalonia.Commands;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.Services;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_Core.Components.Creation;
using Shouldly;
using UnitTests.Helpers;
using Xunit;
using Moq;

namespace UnitTests.ViewModels;

/// <summary>
/// Integration tests for Undo/Redo functionality in MainViewModel.
/// Tests the complete flow: User action → Command → CommandManager → UI update.
/// </summary>
[Trait("Category", "Slow")]
public class UndoRedoIntegrationTests
{
    [Fact]
    public void UndoCommand_WhenHistoryIsEmpty_IsDisabled()
    {
        // Arrange
        var mainVm = CreateMainViewModel();

        // Assert
        mainVm.UndoCommand.CanExecute(null).ShouldBeFalse("Undo command should be disabled when history is empty");
        mainVm.CommandManager.CanUndo.ShouldBeFalse();
    }

    [Fact]
    public void RedoCommand_WhenHistoryIsEmpty_IsDisabled()
    {
        // Arrange
        var mainVm = CreateMainViewModel();

        // Assert
        mainVm.RedoCommand.CanExecute(null).ShouldBeFalse("Redo command should be disabled when history is empty");
        mainVm.CommandManager.CanRedo.ShouldBeFalse();
    }

    [Fact]
    public void UndoCommand_AfterMoveComponent_BecomesEnabled()
    {
        // Arrange
        var mainVm = CreateMainViewModel();
        var component = TestComponentFactory.CreateBasicComponent();
        component.PhysicalX = 100;
        component.PhysicalY = 200;
        var vm = mainVm.Canvas.AddComponent(component, "TestTemplate");

        // Act - Move component (simulates user action)
        var cmd = new MoveComponentCommand(mainVm.Canvas, vm, 100, 200, 150, 250);
        mainVm.CommandManager.ExecuteCommand(cmd);

        // Assert
        mainVm.UndoCommand.CanExecute(null).ShouldBeTrue("Undo command should be enabled after moving a component");
        mainVm.CommandManager.CanUndo.ShouldBeTrue();
        mainVm.CommandManager.UndoCount.ShouldBe(1);
    }

    [Fact]
    public void UndoCommand_Execute_UndoesLastAction()
    {
        // Arrange
        var mainVm = CreateMainViewModel();
        var component = TestComponentFactory.CreateBasicComponent();
        var vm = mainVm.Canvas.AddComponent(component, "TestTemplate");
        var cmd = new DeleteComponentCommand(mainVm.Canvas, vm);
        mainVm.CommandManager.ExecuteCommand(cmd);

        // Act - Execute Undo
        mainVm.UndoCommand.Execute(null);

        // Assert
        mainVm.Canvas.Components.Count.ShouldBe(1, "Component should be restored after undo");
        mainVm.UndoCommand.CanExecute(null).ShouldBeFalse("Undo should be disabled after undoing the only command");
        mainVm.RedoCommand.CanExecute(null).ShouldBeTrue("Redo should be enabled after undo");
    }

    [Fact]
    public void RedoCommand_AfterUndo_BecomesEnabled()
    {
        // Arrange
        var mainVm = CreateMainViewModel();
        var component = TestComponentFactory.CreateBasicComponent();
        var vm = mainVm.Canvas.AddComponent(component, "TestTemplate");
        var cmd = new DeleteComponentCommand(mainVm.Canvas, vm);
        mainVm.CommandManager.ExecuteCommand(cmd);

        // Act - Undo the action
        mainVm.UndoCommand.Execute(null);

        // Assert
        mainVm.RedoCommand.CanExecute(null).ShouldBeTrue("Redo command should be enabled after undo");
        mainVm.CommandManager.CanRedo.ShouldBeTrue();
        mainVm.CommandManager.RedoCount.ShouldBe(1);
    }

    [Fact]
    public void RedoCommand_Execute_RedoesLastUndoneAction()
    {
        // Arrange
        var mainVm = CreateMainViewModel();
        var component = TestComponentFactory.CreateBasicComponent();
        var vm = mainVm.Canvas.AddComponent(component, "TestTemplate");
        var cmd = new DeleteComponentCommand(mainVm.Canvas, vm);
        mainVm.CommandManager.ExecuteCommand(cmd);
        mainVm.UndoCommand.Execute(null);

        // Act - Execute Redo
        mainVm.RedoCommand.Execute(null);

        // Assert
        mainVm.Canvas.Components.Count.ShouldBe(0, "Component should be deleted again after redo");
        mainVm.UndoCommand.CanExecute(null).ShouldBeTrue("Undo should be enabled after redo");
        mainVm.RedoCommand.CanExecute(null).ShouldBeFalse("Redo should be disabled after redoing the only command");
    }

    [Fact]
    public void UndoRedo_MultipleDeletes_WorksCorrectly()
    {
        // Arrange
        var mainVm = CreateMainViewModel();

        // Add 3 components
        var components = new List<ComponentViewModel>();
        for (int i = 0; i < 3; i++)
        {
            var component = TestComponentFactory.CreateBasicComponent();
            component.PhysicalX = i * 100;
            var vm = mainVm.Canvas.AddComponent(component, $"TestTemplate{i}");
            components.Add(vm);
        }

        // Act - Delete all 3 components (using commands)
        foreach (var vm in components)
        {
            var cmd = new DeleteComponentCommand(mainVm.Canvas, vm);
            mainVm.CommandManager.ExecuteCommand(cmd);
        }

        mainVm.Canvas.Components.Count.ShouldBe(0);
        mainVm.CommandManager.UndoCount.ShouldBe(3);

        // Act - Undo twice
        mainVm.UndoCommand.Execute(null);
        mainVm.UndoCommand.Execute(null);

        // Assert after 2 undos
        mainVm.Canvas.Components.Count.ShouldBe(2, "Should have 2 components after 2 undos");
        mainVm.CommandManager.UndoCount.ShouldBe(1);
        mainVm.CommandManager.RedoCount.ShouldBe(2);
        mainVm.UndoCommand.CanExecute(null).ShouldBeTrue("Can still undo");
        mainVm.RedoCommand.CanExecute(null).ShouldBeTrue("Can redo");

        // Act - Redo once
        mainVm.RedoCommand.Execute(null);

        // Assert after 1 redo
        mainVm.Canvas.Components.Count.ShouldBe(1, "Should have 1 component after 1 redo");
        mainVm.CommandManager.UndoCount.ShouldBe(2);
        mainVm.CommandManager.RedoCount.ShouldBe(1);
    }

    [Fact]
    public void CommandManager_AfterNewAction_ClearsRedoStack()
    {
        // Arrange
        var mainVm = CreateMainViewModel();
        var component1 = TestComponentFactory.CreateBasicComponent();
        var vm1 = mainVm.Canvas.AddComponent(component1, "TestTemplate1");
        var cmd1 = new DeleteComponentCommand(mainVm.Canvas, vm1);
        mainVm.CommandManager.ExecuteCommand(cmd1);

        // Undo the action
        mainVm.UndoCommand.Execute(null);
        mainVm.RedoCommand.CanExecute(null).ShouldBeTrue("Should be able to redo");

        // Act - Execute a new action (should clear redo stack)
        var component2 = TestComponentFactory.CreateBasicComponent();
        var vm2 = mainVm.Canvas.AddComponent(component2, "TestTemplate2");
        var cmd2 = new DeleteComponentCommand(mainVm.Canvas, vm2);
        mainVm.CommandManager.ExecuteCommand(cmd2);

        // Assert
        mainVm.RedoCommand.CanExecute(null).ShouldBeFalse("Redo should be disabled after new action");
        mainVm.CommandManager.CanRedo.ShouldBeFalse();
        mainVm.CommandManager.RedoCount.ShouldBe(0, "Redo stack should be empty");
    }

    [Fact]
    public void MoveComponent_CreatesCommandInHistory()
    {
        // Arrange
        var mainVm = CreateMainViewModel();
        var component = TestComponentFactory.CreateBasicComponent();
        component.PhysicalX = 100;
        component.PhysicalY = 200;
        var vm = mainVm.Canvas.AddComponent(component, "TestTemplate");

        var initialX = vm.X;
        var initialY = vm.Y;

        // Act - Move the component using CommandManager
        var moveCmd = new MoveComponentCommand(mainVm.Canvas, vm, initialX, initialY, initialX + 50, initialY + 30);
        mainVm.CommandManager.ExecuteCommand(moveCmd);

        // Assert
        vm.X.ShouldBe(initialX + 50, "Component should be at new X position");
        vm.Y.ShouldBe(initialY + 30, "Component should be at new Y position");
        mainVm.CommandManager.UndoCount.ShouldBe(1, "Move should be in history");
        mainVm.UndoCommand.CanExecute(null).ShouldBeTrue();

        // Act - Undo the move
        mainVm.UndoCommand.Execute(null);

        // Assert - Component should be back at original position
        vm.X.ShouldBe(initialX, "Component should be back at original X position");
        vm.Y.ShouldBe(initialY, "Component should be back at original Y position");
    }

    [Fact]
    public void DeleteComponent_UndoRestoresComponent()
    {
        // Arrange
        var mainVm = CreateMainViewModel();
        var component = TestComponentFactory.CreateBasicComponent();
        var vm = mainVm.Canvas.AddComponent(component, "TestTemplate");
        var originalName = vm.Name;

        // Act - Delete the component
        var deleteCmd = new DeleteComponentCommand(mainVm.Canvas, vm);
        mainVm.CommandManager.ExecuteCommand(deleteCmd);

        // Assert
        mainVm.Canvas.Components.Count.ShouldBe(0, "Component should be deleted");

        // Act - Undo the delete
        mainVm.UndoCommand.Execute(null);

        // Assert
        mainVm.Canvas.Components.Count.ShouldBe(1, "Component should be restored");
        mainVm.Canvas.Components[0].Name.ShouldBe(originalName, "Restored component should have same name");
    }

    private static MainViewModel CreateMainViewModel() =>
        MainViewModelTestHelper.CreateMainViewModel();
}
