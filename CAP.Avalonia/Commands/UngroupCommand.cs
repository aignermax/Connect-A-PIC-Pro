using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Core;
using CAP_Core.Components.Connections;

namespace CAP.Avalonia.Commands;

/// <summary>
/// Command to explode a ComponentGroup back to individual components.
/// Unfreezes internal paths and restores waveguide connections.
/// Preserves object identity across undo/redo cycles to prevent shadow components.
/// </summary>
public class UngroupCommand : IUndoableCommand
{
    private readonly DesignCanvasViewModel _canvas;
    private readonly ComponentGroup _group;
    private ComponentViewModel? _groupViewModel;
    private readonly List<ComponentViewModel> _restoredComponentViewModels = new();
    private readonly List<WaveguideConnectionViewModel> _restoredConnectionViewModels = new();
    private readonly double _groupX;
    private readonly double _groupY;
    private bool _hasExecutedOnce;

    /// <summary>
    /// Creates an ungroup command for the given group.
    /// </summary>
    public UngroupCommand(DesignCanvasViewModel canvas, ComponentGroup group)
    {
        _canvas = canvas;
        _group = group;
        _groupX = group.PhysicalX;
        _groupY = group.PhysicalY;
    }

    /// <inheritdoc />
    public string Description => $"Ungroup {_group.GroupName}";

    /// <inheritdoc />
    public void Execute()
    {
        // On Redo, reuse stored ViewModels instead of creating new ones
        if (_hasExecutedOnce)
        {
            ReAddRestoredComponents();
            return;
        }

        // First execution
        _groupViewModel = _canvas.Components.FirstOrDefault(c => c.Component == _group);
        if (_groupViewModel == null)
            return;

        try
        {
            _canvas.BeginCommandExecution();

            // 1. Extract child components from group
            var children = _group.ChildComponents.ToList();
            _restoredComponentViewModels.Clear();

            foreach (var child in children)
            {
                child.ParentGroup = null;
                var childVm = _canvas.AddComponent(child);
                _restoredComponentViewModels.Add(childVm);
            }

            // 2. Convert frozen paths back to WaveguideConnections
            _restoredConnectionViewModels.Clear();
            foreach (var frozenPath in _group.InternalPaths)
            {
                var connection = new WaveguideConnection
                {
                    StartPin = frozenPath.StartPin,
                    EndPin = frozenPath.EndPin,
                    PropagationLossDbPerCm = _canvas.ConnectionManager.DefaultPropagationLossDbPerCm,
                    BendLossDbPer90Deg = _canvas.ConnectionManager.DefaultBendLossDbPer90Deg
                };

                // Restore the per-connection routing settings (style, radius, width,
                // freeze flag, bend overrides, loss) captured when the group was
                // created; without this the restored connection reverts to "Auto".
                frozenPath.ApplySettingsTo(connection);

                // Re-attach the frozen geometry so a frozen route (and its manual
                // bend edits) survives the re-route triggered below: RecalculateRoutes
                // only keeps frozen/hand-edited paths whose endpoints still match.
                // DeepCopy is essential: the group's InternalPaths stay stored for
                // Undo, and a shared RoutedPath would let later canvas edits (bend
                // handles mutate segments in place) corrupt the undone group's
                // geometry. Both steps happen BEFORE the manager add so the published
                // connection is never half-initialized; the manager itself guards its
                // Connections list against the concurrently running routing pass
                // (mutations locked, routing enumerates a snapshot).
                if (frozenPath.Path != null && frozenPath.Path.Segments.Count > 0)
                {
                    connection.RestoreCachedPath(frozenPath.Path.DeepCopy());
                }

                _canvas.ConnectionManager.AddExistingConnection(connection);

                var connVm = new WaveguideConnectionViewModel(connection);
                _canvas.Connections.Add(connVm);
                _restoredConnectionViewModels.Add(connVm);
            }

            // 3. Remove the group from canvas
            _canvas.RemoveComponent(_groupViewModel);
        }
        finally
        {
            _canvas.EndCommandExecution();
        }

        _hasExecutedOnce = true;

        // Update routes for the restored connections
        _ = _canvas.RecalculateRoutesAsync();
        _canvas.InvalidateSimulation();
    }

    /// <inheritdoc />
    public void Undo()
    {
        if (_groupViewModel == null)
            return;

        try
        {
            _canvas.BeginCommandExecution();

            // Remove restored connections
            foreach (var connVm in _restoredConnectionViewModels)
            {
                _canvas.Connections.Remove(connVm);
                _canvas.ConnectionManager.RemoveConnectionDeferred(connVm.Connection);
            }

            // Remove individual components
            foreach (var compVm in _restoredComponentViewModels)
            {
                compVm.Component.ParentGroup = _group;

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

            // Re-add the group using the SAME ViewModel
            _group.PhysicalX = _groupX;
            _group.PhysicalY = _groupY;
            _canvas.Components.Add(_groupViewModel);
            _canvas.Router.AddComponentObstacle(_group);

            foreach (var pin in _group.ExternalPins)
            {
                _canvas.AllPins.Add(new PinViewModel(pin.InternalPin, _groupViewModel));
            }
        }
        finally
        {
            _canvas.EndCommandExecution();
        }

        _ = _canvas.RecalculateRoutesAsync();
        _canvas.InvalidateSimulation();
    }

    /// <summary>
    /// Re-adds previously restored child components on Redo, preserving object identity.
    /// </summary>
    private void ReAddRestoredComponents()
    {
        // Find the group's current ViewModel on canvas
        _groupViewModel = _canvas.Components.FirstOrDefault(c => c.Component == _group);
        if (_groupViewModel == null)
            return;

        try
        {
            _canvas.BeginCommandExecution();

            // Re-add the SAME child ViewModels
            foreach (var childVm in _restoredComponentViewModels)
            {
                childVm.Component.ParentGroup = null;
                _canvas.Components.Add(childVm);
                _canvas.Router.AddComponentObstacle(childVm.Component);

                foreach (var pin in childVm.Component.PhysicalPins)
                {
                    _canvas.AllPins.Add(new PinViewModel(pin, childVm));
                }
            }

            // Re-add the SAME connection ViewModels
            foreach (var connVm in _restoredConnectionViewModels)
            {
                _canvas.ConnectionManager.AddExistingConnection(connVm.Connection);
                _canvas.Connections.Add(connVm);
            }

            // Remove the group from canvas
            _canvas.RemoveComponent(_groupViewModel);
        }
        finally
        {
            _canvas.EndCommandExecution();
        }

        _ = _canvas.RecalculateRoutesAsync();
        _canvas.InvalidateSimulation();
    }
}
