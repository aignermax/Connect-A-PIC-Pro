using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.Services;
using CAP_Core.Components.Core;
using CAP_Core.Components.Connections;
using CAP_Core.Routing;

namespace CAP.Avalonia.Commands;

/// <summary>
/// Command to create a ComponentGroup from selected components.
/// Captures current positions and waveguide paths as frozen geometry.
/// Does NOT automatically save to library - use SaveGroupAsPrefabCommand for that.
/// </summary>
public class CreateGroupCommand : IUndoableCommand
{
    private readonly DesignCanvasViewModel _canvas;
    private readonly List<Component> _components;
    private readonly List<ComponentViewModel> _componentViewModels = new(); // STORE ViewModels!
    private readonly string? _requestedGroupName;
    private ComponentGroup? _createdGroup;
    private ComponentViewModel? _groupViewModel;
    private readonly List<WaveguideConnection> _internalConnections = new();
    private readonly List<WaveguideConnection> _externalConnections = new();
    private readonly List<WaveguideConnectionViewModel> _internalConnectionViewModels = new();
    private readonly Dictionary<Component, (double x, double y)> _originalPositions = new();

    public CreateGroupCommand(
        DesignCanvasViewModel canvas,
        List<ComponentViewModel> components)
        : this(canvas, components, null)
    {
    }

    /// <summary>
    /// Creates the command with the group's final name up front. The name is
    /// applied when the <see cref="ComponentGroup"/> is constructed — BEFORE the
    /// group ViewModel is added to the canvas and selected — so bound panels never
    /// observe the placeholder <c>Group_HHmmss</c> name (a post-selection rename
    /// leaves <c>ComponentViewModel.DisplayName</c> stale; it has no change
    /// notification).
    /// </summary>
    /// <param name="canvas">Canvas the group is created on.</param>
    /// <param name="components">Components to group.</param>
    /// <param name="requestedGroupName">
    /// Final group name (e.g. an imported GDS top cell); null or whitespace keeps
    /// the timestamped default.
    /// </param>
    public CreateGroupCommand(
        DesignCanvasViewModel canvas,
        List<ComponentViewModel> components,
        string? requestedGroupName)
    {
        _canvas = canvas;
        _components = components.Select(c => c.Component).ToList();
        _requestedGroupName = string.IsNullOrWhiteSpace(requestedGroupName) ? null : requestedGroupName;

        // Store original positions
        foreach (var comp in _components)
        {
            _originalPositions[comp] = (comp.PhysicalX, comp.PhysicalY);
        }
    }

    public string Description => $"Create group from {_components.Count} components";

    /// <summary>The group created by <see cref="Execute"/> (null until the first execution).</summary>
    public ComponentGroup? CreatedGroup => _createdGroup;

    public void Execute()
    {
        if (_components.Count < 2)
            return;

        // Don't group locked components
        if (_components.Any(c => c.IsLocked))
            return;

        // If group already exists (Redo scenario), just re-add it to canvas
        if (_createdGroup != null && _groupViewModel != null)
        {
            try
            {
                _canvas.BeginCommandExecution();

                // Remove child components from canvas
                // IMPORTANT: Find ViewModels by Core Component reference, not by stored ViewModel reference
                // This handles the case where components were removed/re-added (creating new ViewModels)
                var componentsToRemove = _canvas.Components
                    .Where(cvm => _components.Contains(cvm.Component))
                    .ToList();

                foreach (var compVm in componentsToRemove)
                {
                    var pinsToRemove = _canvas.AllPins
                        .Where(p => p.ParentComponentViewModel == compVm)
                        .ToList();
                    foreach (var pin in pinsToRemove)
                    {
                        _canvas.AllPins.Remove(pin);
                    }
                    _canvas.Router.RemoveComponentObstacle(compVm.Component);
                    _canvas.Components.Remove(compVm);
                }

                // Remove internal connections
                foreach (var connVm in _internalConnectionViewModels)
                {
                    _canvas.Connections.Remove(connVm);
                    _canvas.ConnectionManager.RemoveConnectionDeferred(connVm.Connection);
                }

                // Re-add the SAME group ViewModel and Router obstacle
                _canvas.Components.Add(_groupViewModel);
                _canvas.Router.AddComponentObstacle(_createdGroup);

                // Re-add group pins
                foreach (var pin in _createdGroup.ExternalPins)
                {
                    _canvas.AllPins.Add(new PinViewModel(pin.InternalPin, _groupViewModel));
                }

                _canvas.Selection.SelectSingle(_groupViewModel);
                _canvas.SelectedComponent = _groupViewModel;
            }
            finally
            {
                _canvas.EndCommandExecution();
            }

            _ = _canvas.RecalculateRoutesAsync();
            _canvas.InvalidateSimulation();
            return;
        }

        // First execution: create new group
        // 1. Calculate bounding box for selected components
        double minX = _components.Min(c => c.PhysicalX);
        double minY = _components.Min(c => c.PhysicalY);
        double maxX = _components.Max(c => c.PhysicalX + c.WidthMicrometers);
        double maxY = _components.Max(c => c.PhysicalY + c.HeightMicrometers);

        // 2. Identify internal vs external waveguide connections
        var componentSet = new HashSet<Component>(_components);
        _internalConnections.Clear();
        _externalConnections.Clear();

        foreach (var conn in _canvas.ConnectionManager.Connections)
        {
            bool startInGroup = componentSet.Contains(conn.StartPin.ParentComponent);
            bool endInGroup = componentSet.Contains(conn.EndPin.ParentComponent);

            if (startInGroup && endInGroup)
            {
                _internalConnections.Add(conn);
            }
            else if (startInGroup || endInGroup)
            {
                _externalConnections.Add(conn);
            }
        }

        // 3. Create ComponentGroup (with the requested final name when given —
        // the group is selected below, so the name must be correct by then)
        _createdGroup = new ComponentGroup(_requestedGroupName ?? $"Group_{DateTime.Now:HHmmss}")
        {
            PhysicalX = minX,
            PhysicalY = minY,
            Description = $"Group of {_components.Count} components"
        };

        // 4. Add child components to group in one batch — per-item adds rescan
        // all children and path segments each time (quadratic at import scale).
        _createdGroup.AddChildren(_components);

        // 5. Create frozen paths for internal connections.
        // Always create a FrozenWaveguidePath even when RoutedPath is null — an empty
        // RoutedPath produces TransmissionCoefficient = Complex.One (lossless), which is
        // the correct conservative default and ensures the connection is preserved in the
        // group S-Matrix. Skipping connections with null RoutedPath silently drops them.
        var frozenPaths = new List<FrozenWaveguidePath>();
        foreach (var conn in _internalConnections)
        {
            var frozenPath = new FrozenWaveguidePath
            {
                Path = conn.RoutedPath?.DeepCopy() ?? new RoutedPath(),
                StartPin = conn.StartPin,
                EndPin = conn.EndPin
            };
            // Preserve the per-connection routing settings (style, radius, width,
            // freeze flag, bend overrides, loss) so group edit mode, ungroup and
            // saved templates restore them instead of "Auto" defaults.
            frozenPath.CaptureSettingsFrom(conn);
            frozenPaths.Add(frozenPath);
        }
        _createdGroup.AddInternalPaths(frozenPaths);

        // 6. Create GroupPins for ALL unoccupied pins
        var occupiedPins = new HashSet<PhysicalPin>();

        // Mark pins that are occupied by internal connections
        foreach (var conn in _internalConnections)
        {
            occupiedPins.Add(conn.StartPin);
            occupiedPins.Add(conn.EndPin);
        }

        // Expose all unoccupied pins as external pins
        foreach (var comp in _components)
        {
            foreach (var pin in comp.PhysicalPins)
            {
                if (!occupiedPins.Contains(pin))
                {
                    var (pinX, pinY) = pin.GetAbsolutePosition();
                    var groupPin = new GroupPin
                    {
                        Name = $"{pin.ParentComponent.Identifier}_{pin.Name}",
                        InternalPin = pin,
                        RelativeX = pinX - _createdGroup.PhysicalX,
                        RelativeY = pinY - _createdGroup.PhysicalY,
                        AngleDegrees = pin.GetAbsoluteAngle()
                    };
                    _createdGroup.AddExternalPin(groupPin);
                }
            }
        }

        try
        {
            _canvas.BeginCommandExecution();

            // 7. Store and remove individual components from canvas.
            // Set/dictionary lookups keep this O(N + P + C): the previous
            // per-item LINQ scans made grouping a 5000-component import
            // quadratic (minutes on the UI thread).
            _componentViewModels.Clear();
            var groupedComponents = new HashSet<Component>(_components);
            var componentsToRemove = _canvas.Components
                .Where(cvm => groupedComponents.Contains(cvm.Component))
                .ToList();

            // Store ComponentViewModels so we can restore them in Undo!
            _componentViewModels.AddRange(componentsToRemove);

            // Store internal connection ViewModels, then remove them from the
            // canvas (they're now frozen in the group).
            _internalConnectionViewModels.Clear();
            var internalConnectionSet = new HashSet<WaveguideConnection>(_internalConnections);
            _internalConnectionViewModels.AddRange(
                _canvas.Connections.Where(c => internalConnectionSet.Contains(c.Connection)));
            foreach (var connVm in _internalConnectionViewModels)
            {
                _canvas.Connections.Remove(connVm);
                _canvas.ConnectionManager.RemoveConnectionDeferred(connVm.Connection);
            }

            var removedViewModels = new HashSet<ComponentViewModel>(componentsToRemove);
            var pinsToRemove = _canvas.AllPins
                .Where(p => p.ParentComponentViewModel is ComponentViewModel parent
                            && removedViewModels.Contains(parent))
                .ToList();
            foreach (var pin in pinsToRemove)
            {
                _canvas.AllPins.Remove(pin);
            }
            foreach (var compVm in componentsToRemove)
            {
                _canvas.Components.Remove(compVm);
            }

            // 8. Add group to canvas
            _groupViewModel = _canvas.AddComponent(_createdGroup);

            // 9. Select the newly created group so user gets visual feedback
            _canvas.Selection.SelectSingle(_groupViewModel);
            _canvas.SelectedComponent = _groupViewModel;
        }
        finally
        {
            _canvas.EndCommandExecution();
        }

        // Recalculate routes for external connections
        _ = _canvas.RecalculateRoutesAsync();
        _canvas.InvalidateSimulation();

        // NOTE: Groups are NOT auto-saved to library anymore.
        // User must explicitly use "Save as Prefab" action.
    }

    public void Undo()
    {
        if (_createdGroup == null || _groupViewModel == null)
            return;

        try
        {
            _canvas.BeginCommandExecution();

            // Remove the group
            _canvas.RemoveComponent(_groupViewModel);

            // Restore individual components at their original positions
            // CRITICAL: Re-add the SAME ComponentViewModels we removed!
            foreach (var compVm in _componentViewModels)
            {
                var comp = compVm.Component;
                if (_originalPositions.TryGetValue(comp, out var pos))
                {
                    comp.PhysicalX = pos.x;
                    comp.PhysicalY = pos.y;
                    // Sync the ViewModel's cached position with the model
                    compVm.X = pos.x;
                    compVm.Y = pos.y;
                }
                comp.ParentGroup = null;

                // Restore the SAME ViewModel (not create a new one!)
                _canvas.Components.Add(compVm);
                _canvas.Router.AddComponentObstacle(comp);

                // Re-add pins to AllPins
                foreach (var pin in comp.PhysicalPins)
                {
                    _canvas.AllPins.Add(new PinViewModel(pin, compVm));
                }
            }

            // Restore internal connections
            foreach (var connVm in _internalConnectionViewModels)
            {
                _canvas.ConnectionManager.AddExistingConnection(connVm.Connection);
                _canvas.Connections.Add(connVm);
            }
        }
        finally
        {
            _canvas.EndCommandExecution();
        }

        // Recalculate routes
        _ = _canvas.RecalculateRoutesAsync();
        _canvas.InvalidateSimulation();
    }

}
