using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CAP_Core.Grid;
using CAP.Avalonia.Commands;
using CAP.Avalonia.ViewModels.Canvas;

namespace CAP.Avalonia.ViewModels.Library;

/// <summary>
/// ViewModel for locking/unlocking components and connections.
/// Provides commands to lock selected elements to prevent accidental modification.
/// </summary>
public partial class ElementLockViewModel : ObservableObject
{
    private readonly LockManager _lockManager;
    private Commands.CommandManager? _commandManager;

    [ObservableProperty]
    private DesignCanvasViewModel? _canvas;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private int _lockedComponentCount;

    [ObservableProperty]
    private int _lockedConnectionCount;

    public ElementLockViewModel()
    {
        _lockManager = new LockManager();
    }

    /// <summary>
    /// Configures the ViewModel with the design canvas and command manager.
    /// </summary>
    public void Configure(DesignCanvasViewModel canvas, Commands.CommandManager commandManager)
    {
        Canvas = canvas;
        _commandManager = commandManager;

        // Subscribe to selection changes to update command availability
        Canvas.Selection.SelectedComponents.CollectionChanged += (s, e) =>
        {
            RefreshCommands();
        };

        UpdateLockCounts();
    }

    /// <summary>
    /// Locks the selected components.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanLockSelectedComponents))]
    private void LockSelectedComponents()
    {
        if (Canvas == null || _commandManager == null)
            return;

        var selectedComponents = Canvas.Selection.SelectedComponents
            .Select(vm => vm.Component)
            .Where(c => !c.IsLocked)
            .ToList();

        if (selectedComponents.Any())
        {
            var cmd = new LockComponentsCommand(_lockManager, selectedComponents, Canvas);
            _commandManager.ExecuteCommand(cmd);
            StatusText = $"Locked {selectedComponents.Count} component(s)";
            UpdateLockCounts();
            RefreshCommands(); // Update button states immediately
        }
    }

    private bool CanLockSelectedComponents()
    {
        return Canvas?.Selection.SelectedComponents.Any(vm => !vm.Component.IsLocked) ?? false;
    }

    /// <summary>
    /// Unlocks the selected components.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUnlockSelectedComponents))]
    private void UnlockSelectedComponents()
    {
        if (Canvas == null || _commandManager == null)
            return;

        var selectedComponents = Canvas.Selection.SelectedComponents
            .Select(vm => vm.Component)
            .Where(c => c.IsLocked)
            .ToList();

        if (selectedComponents.Any())
        {
            var cmd = new UnlockComponentsCommand(_lockManager, selectedComponents, Canvas);
            _commandManager.ExecuteCommand(cmd);
            StatusText = $"Unlocked {selectedComponents.Count} component(s)";
            UpdateLockCounts();
            RefreshCommands(); // Update button states immediately
        }
    }

    private bool CanUnlockSelectedComponents()
    {
        return Canvas?.Selection.SelectedComponents.Any(vm => vm.Component.IsLocked) ?? false;
    }

    /// <summary>
    /// Toggles the lock state of selected components.
    /// If any selected component is unlocked, locks all selected. Otherwise unlocks all.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelectedComponents))]
    private void ToggleSelectedComponents()
    {
        if (Canvas == null || _commandManager == null)
            return;

        var selectedComponents = Canvas.Selection.SelectedComponents
            .Select(vm => vm.Component)
            .ToList();

        if (!selectedComponents.Any())
            return;

        // If any component is unlocked, lock all. Otherwise unlock all.
        bool shouldLock = selectedComponents.Any(c => !c.IsLocked);

        if (shouldLock)
        {
            var componentsToLock = selectedComponents.Where(c => !c.IsLocked).ToList();
            if (componentsToLock.Any())
            {
                var cmd = new LockComponentsCommand(_lockManager, componentsToLock, Canvas);
                _commandManager.ExecuteCommand(cmd);
                StatusText = $"Locked {componentsToLock.Count} component(s)";
            }
        }
        else
        {
            var componentsToUnlock = selectedComponents.Where(c => c.IsLocked).ToList();
            if (componentsToUnlock.Any())
            {
                var cmd = new UnlockComponentsCommand(_lockManager, componentsToUnlock, Canvas);
                _commandManager.ExecuteCommand(cmd);
                StatusText = $"Unlocked {componentsToUnlock.Count} component(s)";
            }
        }

        UpdateLockCounts();
        RefreshCommands();
    }

    private bool HasSelectedComponents()
    {
        return Canvas?.Selection.SelectedComponents.Any() ?? false;
    }

    /// <summary>
    /// Unlocks all components in the design.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasLockedComponents))]
    private void UnlockAllComponents()
    {
        if (Canvas == null || _commandManager == null)
            return;

        var allComponents = Canvas.Components.Select(vm => vm.Component).ToList();
        var lockedComponents = _lockManager.GetLockedComponents(allComponents).ToList();

        if (lockedComponents.Any())
        {
            var cmd = new UnlockComponentsCommand(_lockManager, lockedComponents, Canvas);
            _commandManager.ExecuteCommand(cmd);
            StatusText = $"Unlocked all {lockedComponents.Count} component(s)";
            UpdateLockCounts();
        }
    }

    private bool HasLockedComponents()
    {
        if (Canvas == null)
            return false;

        return Canvas.Components.Any(vm => vm.Component.IsLocked);
    }

    /// <summary>
    /// Locks selected connections.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelectedConnections))]
    private void LockSelectedConnections()
    {
        if (Canvas == null)
            return;

        // Note: Connection selection would need to be implemented in the canvas
        // For now, this is a placeholder for future connection selection support
        StatusText = "Connection selection not yet implemented";
    }

    private bool HasSelectedConnections()
    {
        // Placeholder for connection selection
        return false;
    }

    /// <summary>
    /// Unlocks all connections in the design.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasLockedConnections))]
    private void UnlockAllConnections()
    {
        if (Canvas == null)
            return;

        var lockedConnections = Canvas.Connections
            .Select(vm => vm.Connection)
            .Where(c => c.IsLocked)
            .ToList();

        if (lockedConnections.Any())
        {
            _lockManager.UnlockConnections(lockedConnections);
            StatusText = $"Unlocked all {lockedConnections.Count} connection(s)";
            UpdateLockCounts();
        }
    }

    private bool HasLockedConnections()
    {
        if (Canvas == null)
            return false;

        return Canvas.Connections.Any(vm => vm.Connection.IsLocked);
    }

    /// <summary>
    /// Unlocks every locked component and connection in one step. Backs the combined
    /// "Unlock All" entry in the toolbar's Tools menu (the former per-kind buttons lived
    /// in the now-removed "Lock Elements" properties-panel section).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUnlockAll))]
    private void UnlockAll()
    {
        if (UnlockAllComponentsCommand.CanExecute(null))
            UnlockAllComponentsCommand.Execute(null);

        if (UnlockAllConnectionsCommand.CanExecute(null))
            UnlockAllConnectionsCommand.Execute(null);
    }

    private bool CanUnlockAll() => HasLockedComponents() || HasLockedConnections();

    /// <summary>
    /// Updates the count of locked elements for display.
    /// </summary>
    private void UpdateLockCounts()
    {
        if (Canvas == null)
        {
            LockedComponentCount = 0;
            LockedConnectionCount = 0;
            return;
        }

        LockedComponentCount = Canvas.Components.Count(vm => vm.Component.IsLocked);
        LockedConnectionCount = Canvas.Connections.Count(vm => vm.Connection.IsLocked);
    }

    /// <summary>
    /// Refreshes command availability states.
    /// Call this when selection changes or lock states change externally.
    /// </summary>
    public void RefreshCommands()
    {
        UpdateLockCounts();
        LockSelectedComponentsCommand.NotifyCanExecuteChanged();
        UnlockSelectedComponentsCommand.NotifyCanExecuteChanged();
        ToggleSelectedComponentsCommand.NotifyCanExecuteChanged();
        UnlockAllComponentsCommand.NotifyCanExecuteChanged();
        LockSelectedConnectionsCommand.NotifyCanExecuteChanged();
        UnlockAllConnectionsCommand.NotifyCanExecuteChanged();
        UnlockAllCommand.NotifyCanExecuteChanged();
    }
}
