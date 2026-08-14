using System.Collections.ObjectModel;
using CAP_Core.Components;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Routing;

namespace CAP.Avalonia.ViewModels.Canvas.Services;

/// <summary>
/// Manages group editing operations (Unity-style sub-canvas approach).
/// Handles entering/exiting edit mode, breadcrumb navigation, and canvas state backup/restore.
/// </summary>
public class GroupEditService
{
    private readonly ObservableCollection<ComponentViewModel> _components;
    private readonly ObservableCollection<WaveguideConnectionViewModel> _connections;
    private readonly ObservableCollection<PinViewModel> _allPins;
    private readonly WaveguideConnectionManager _connectionManager;
    private readonly WaveguideRouter _router;
    private readonly Func<Component, string?, string?, ComponentViewModel> _addComponent;
    private readonly Action _beginCommandExecution;
    private readonly Action _endCommandExecution;
    private readonly Action _initializeRouting;
    private readonly Func<Task> _recalculateRoutes;

    private readonly Stack<ComponentGroup> _editModeStack = new();
    private CanvasState? _rootCanvasBackup;

    /// <summary>
    /// The currently edited group (null if at root level).
    /// </summary>
    public ComponentGroup? CurrentEditGroup { get; private set; }

    /// <summary>
    /// Whether currently in group edit mode.
    /// </summary>
    public bool IsInGroupEditMode => CurrentEditGroup != null;

    /// <summary>
    /// Breadcrumb path from root to current edit group.
    /// </summary>
    public ObservableCollection<ComponentGroup> BreadcrumbPath { get; } = new();

    /// <summary>
    /// Raised when group edit state changes (for VM to update bindings).
    /// </summary>
    public event Action? EditStateChanged;

    /// <summary>
    /// Called immediately when CurrentEditGroup changes, BEFORE collections are modified.
    /// This allows the VM to update its observable property before CollectionChanged fires.
    /// </summary>
    public event Action<ComponentGroup?>? CurrentEditGroupChanging;

    /// <summary>
    /// Initializes the group edit service with required dependencies.
    /// </summary>
    public GroupEditService(
        ObservableCollection<ComponentViewModel> components,
        ObservableCollection<WaveguideConnectionViewModel> connections,
        ObservableCollection<PinViewModel> allPins,
        WaveguideConnectionManager connectionManager,
        WaveguideRouter router,
        Func<Component, string?, string?, ComponentViewModel> addComponent,
        Action beginCommandExecution,
        Action endCommandExecution,
        Action initializeRouting,
        Func<Task> recalculateRoutes)
    {
        _components = components;
        _connections = connections;
        _allPins = allPins;
        _connectionManager = connectionManager;
        _router = router;
        _addComponent = addComponent;
        _beginCommandExecution = beginCommandExecution;
        _endCommandExecution = endCommandExecution;
        _initializeRouting = initializeRouting;
        _recalculateRoutes = recalculateRoutes;
    }

    /// <summary>
    /// Enters edit mode for a ComponentGroup (Unity-style sub-canvas approach).
    /// </summary>
    public void EnterGroupEditMode(ComponentGroup group)
    {
        if (group == null)
            throw new ArgumentNullException(nameof(group));

        if (CurrentEditGroup == null)
        {
            _rootCanvasBackup = BackupCanvasState();
        }
        else
        {
            _editModeStack.Push(CurrentEditGroup);
        }

        CurrentEditGroup = group;
        CurrentEditGroupChanging?.Invoke(CurrentEditGroup);
        LoadGroupAsSubCanvas(group);
        UpdateBreadcrumbPath();
        EditStateChanged?.Invoke();
    }

    /// <summary>
    /// Exits the current group edit mode.
    /// </summary>
    public void ExitGroupEditMode()
    {
        if (CurrentEditGroup == null)
            return;

        var editedGroup = CurrentEditGroup;
        SaveSubCanvasToGroup(editedGroup);

        if (_editModeStack.Count > 0)
        {
            var parentGroup = _editModeStack.Pop();
            CurrentEditGroup = parentGroup;
            CurrentEditGroupChanging?.Invoke(CurrentEditGroup);
            LoadGroupAsSubCanvas(parentGroup);
        }
        else
        {
            CurrentEditGroup = null;
            CurrentEditGroupChanging?.Invoke(null);
            if (_rootCanvasBackup != null)
            {
                RestoreCanvasState(_rootCanvasBackup);
                _rootCanvasBackup = null;
            }
        }

        NotifyGroupDimensionsChanged(editedGroup);
        UpdateBreadcrumbPath();
        EditStateChanged?.Invoke();
    }

    /// <summary>
    /// Exits all the way to root level.
    /// </summary>
    public void ExitToRoot()
    {
        if (CurrentEditGroup == null)
            return;

        var editedGroup = CurrentEditGroup;
        SaveSubCanvasToGroup(editedGroup);

        while (_editModeStack.Count > 0)
            _editModeStack.Pop();

        CurrentEditGroup = null;
        CurrentEditGroupChanging?.Invoke(null);
        if (_rootCanvasBackup != null)
        {
            RestoreCanvasState(_rootCanvasBackup);
            _rootCanvasBackup = null;
        }

        NotifyGroupDimensionsChanged(editedGroup);
        _editModeStack.Clear();
        UpdateBreadcrumbPath();
        EditStateChanged?.Invoke();
    }

    /// <summary>
    /// Jumps to a specific level in the breadcrumb path.
    /// </summary>
    public void NavigateToBreadcrumbLevel(ComponentGroup? group)
    {
        if (group == null)
        {
            ExitToRoot();
            return;
        }

        var index = BreadcrumbPath.IndexOf(group);
        if (index < 0)
            return;

        while (_editModeStack.Count > BreadcrumbPath.Count - index - 2)
            _editModeStack.Pop();

        CurrentEditGroup = group;
        CurrentEditGroupChanging?.Invoke(CurrentEditGroup);
        UpdateBreadcrumbPath();
        EditStateChanged?.Invoke();
    }

    /// <summary>
    /// Updates external pin positions for a group based on current child positions.
    /// </summary>
    public void UpdateExternalPinPositions(ComponentGroup group)
    {
        foreach (var externalPin in group.ExternalPins)
        {
            var (pinX, pinY) = externalPin.InternalPin.GetAbsolutePosition();
            externalPin.RelativeX = pinX - group.PhysicalX;
            externalPin.RelativeY = pinY - group.PhysicalY;
        }
    }

    /// <summary>
    /// Calculates the bounding rectangle for a ComponentGroup.
    /// </summary>
    public (double X, double Y, double Width, double Height) CalculateGroupBounds(ComponentGroup group)
    {
        if (group.ChildComponents.Count == 0)
            return (group.PhysicalX, group.PhysicalY, group.WidthMicrometers, group.HeightMicrometers);

        double minX = group.ChildComponents.Min(c => c.PhysicalX);
        double minY = group.ChildComponents.Min(c => c.PhysicalY);
        double maxX = group.ChildComponents.Max(c => c.PhysicalX + c.WidthMicrometers);
        double maxY = group.ChildComponents.Max(c => c.PhysicalY + c.HeightMicrometers);

        return (minX, minY, maxX - minX, maxY - minY);
    }

    private void UpdateBreadcrumbPath()
    {
        BreadcrumbPath.Clear();
        var tempStack = new Stack<ComponentGroup>(_editModeStack.Reverse());
        while (tempStack.Count > 0)
            BreadcrumbPath.Add(tempStack.Pop());

        if (CurrentEditGroup != null)
            BreadcrumbPath.Add(CurrentEditGroup);
    }

    private CanvasState BackupCanvasState()
    {
        return new CanvasState
        {
            Components = _components.ToList(),
            Connections = _connections.ToList(),
            AllPins = _allPins.ToList(),
            ManagerConnections = _connectionManager.Connections.ToList()
        };
    }

    private void RestoreCanvasState(CanvasState state)
    {
        try
        {
            _beginCommandExecution();
            _components.Clear();
            _connections.Clear();
            _allPins.Clear();
            _connectionManager.Clear();

            foreach (var comp in state.Components)
                _components.Add(comp);
            foreach (var conn in state.Connections)
                _connections.Add(conn);
            foreach (var pin in state.AllPins)
                _allPins.Add(pin);
            foreach (var managerConn in state.ManagerConnections)
                _connectionManager.AddExistingConnection(managerConn);
        }
        finally
        {
            _endCommandExecution();
        }

        _ = _recalculateRoutes();
    }

    private void LoadGroupAsSubCanvas(ComponentGroup group)
    {
        try
        {
            _beginCommandExecution();
            _components.Clear();
            _connections.Clear();
            _allPins.Clear();
            _connectionManager.Clear();

            foreach (var child in group.ChildComponents)
                _addComponent(child, null, null);

            _initializeRouting();

            foreach (var frozenPath in group.InternalPaths)
            {
                // Pin-less frozen geometry (GDS-imported route outlines) cannot become
                // a live connection — it stays stored on the group and is re-attached
                // untouched by SaveSubCanvasToGroup on exit.
                if (frozenPath.StartPin is null || frozenPath.EndPin is null)
                    continue;

                // DeepCopy: the group's stored InternalPaths must stay immutable while
                // the sub-canvas connection is live — bend-handle edits mutate the
                // segment objects in place and would otherwise leak into the stored
                // geometry before the exit re-capture (round-5 review [4]).
                var connection = _connectionManager.AddConnectionWithCachedRoute(
                    frozenPath.StartPin, frozenPath.EndPin, frozenPath.Path?.DeepCopy()!);
                // The cached route restores only the geometry; without this the editor
                // shows a fresh default connection ("Auto") although the curve renders
                // correctly (field report round 5, finding a).
                frozenPath.ApplySettingsTo(connection);
                // AddConnectionWithCachedRoute already computed the transmission — but
                // with the manager's DEFAULT loss, before the stored settings existed.
                // Recompute from the same geometry so simulations in edit mode use the
                // connection's restored PropagationLossDbPerCm (round-5 review [5];
                // UngroupCommand applies settings before restoring and needs no refresh).
                connection.UpdateLossFromPath();
                var connVm = new WaveguideConnectionViewModel(connection);
                _connections.Add(connVm);
            }
        }
        finally
        {
            _endCommandExecution();
        }
    }

    private void SaveSubCanvasToGroup(ComponentGroup group)
    {
        // Set lookups + batched group mutations keep this O(N + P·S): per-item
        // AddChild/RemoveChild/AddInternalPath each rescan all children and all
        // path segments for the bounds, which is quadratic at GDS-import scale.
        var canvasComponents = _components.Select(c => c.Component).ToHashSet();
        group.RemoveChildren(group.ChildComponents.Where(c => !canvasComponents.Contains(c)).ToList());

        var existingChildren = group.ChildComponents.ToHashSet();
        group.AddChildren(_components
            .Select(c => c.Component)
            .Where(c => !existingChildren.Contains(c))
            .ToList());

        // Pin-less frozen paths (GDS-imported route outlines) never entered the
        // sub-canvas as live connections, so the clear-and-rebuild below would
        // silently drop them — capture and re-attach them unchanged.
        var pinLessPaths = group.InternalPaths
            .Where(p => p.StartPin is null || p.EndPin is null)
            .ToList();

        group.InternalPaths.Clear();
        var frozenPaths = new List<FrozenWaveguidePath>();
        foreach (var connVm in _connections.ToList())
        {
            var conn = connVm.Connection;
            if (conn.RoutedPath == null)
                continue;

            var frozenPath = new FrozenWaveguidePath
            {
                StartPin = conn.StartPin,
                EndPin = conn.EndPin,
                Path = conn.RoutedPath
            };
            // Keep the per-connection routing settings across the exit, otherwise
            // they reset to "Auto" defaults on the next expand.
            frozenPath.CaptureSettingsFrom(conn);
            frozenPaths.Add(frozenPath);
        }
        frozenPaths.AddRange(pinLessPaths);
        group.AddInternalPaths(frozenPaths);

        // Still needed when the clear above removed paths but nothing was re-added.
        group.UpdateGroupBounds();
    }

    private void NotifyGroupDimensionsChanged(ComponentGroup editedGroup)
    {
        var groupViewModel = _components.FirstOrDefault(c => c.Component == editedGroup);
        if (groupViewModel == null)
            return;

        groupViewModel.NotifyDimensionsChanged();
        if (editedGroup.ChildComponents.Count > 0)
        {
            groupViewModel.X = editedGroup.ChildComponents.Min(c => c.PhysicalX);
            groupViewModel.Y = editedGroup.ChildComponents.Min(c => c.PhysicalY);
        }
    }

    private class CanvasState
    {
        public List<ComponentViewModel> Components { get; set; } = new();
        public List<WaveguideConnectionViewModel> Connections { get; set; } = new();
        public List<PinViewModel> AllPins { get; set; } = new();
        public List<WaveguideConnection> ManagerConnections { get; set; } = new();
    }
}
