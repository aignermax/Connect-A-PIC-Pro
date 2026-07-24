using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Components.Connections;

namespace CAP.Avalonia.Commands;

/// <summary>
/// Command for deleting a component from the canvas.
/// </summary>
public class DeleteComponentCommand : IUndoableCommand
{
    private readonly DesignCanvasViewModel _canvas;
    private ComponentViewModel _componentViewModel;
    private readonly Component _component;
    private readonly string? _templateName;
    private readonly string? _templatePdkSource;
    private readonly double _x;
    private readonly double _y;
    private readonly int? _laserWavelengthNm;
    private readonly double? _laserInputPower;

    /// <summary>
    /// Connections to restore on Undo. Crossing sub-connections are snapshotted as
    /// their pre-split ORIGINALS (#705): deleting the component dissolves its
    /// crossings, so re-adding the subs would resurrect ghost pins on a removed
    /// crossing component and duplicate the survivors the dissolution restored.
    /// </summary>
    private readonly List<WaveguideConnection> _deletedConnections = new();

    public DeleteComponentCommand(
        DesignCanvasViewModel canvas,
        ComponentViewModel componentViewModel)
    {
        _canvas = canvas;
        _componentViewModel = componentViewModel;
        _component = componentViewModel.Component;
        _templateName = componentViewModel.TemplateName;
        _templatePdkSource = componentViewModel.TemplatePdkSource;
        _x = componentViewModel.X;
        _y = componentViewModel.Y;
        // Wavelength/power live only on the ViewModel's LaserConfig — snapshot them so
        // undo does not silently reset the laser (#690; IsEnabled itself survives on
        // the core component, which is reused by Undo).
        _laserWavelengthNm = componentViewModel.LaserConfig?.WavelengthNm;
        _laserInputPower = componentViewModel.LaserConfig?.InputPower;
    }

    public string Description => $"Delete {_component.Identifier}";

    public void Execute()
    {
        // Don't delete locked components
        if (_component.IsLocked)
            return;

        // Store connections that will be deleted, normalized to pre-split originals
        // (two subs of the same crossing dedupe to one original).
        _deletedConnections.Clear();
        var crossing = _canvas.ConnectionManager.CrossingInsertion;
        foreach (var connVm in _canvas.Connections.ToList())
        {
            // Check if connection is to regular pins or to GroupPins (ExternalPins)
            bool startConnected = IsPinConnectedToComponent(connVm.Connection.StartPin, _component);
            bool endConnected = IsPinConnectedToComponent(connVm.Connection.EndPin, _component);

            if (startConnected || endConnected)
            {
                var original = crossing?.ResolveToOriginal(connVm.Connection) ?? connVm.Connection;
                if (!_deletedConnections.Contains(original))
                    _deletedConnections.Add(original);
            }
        }

        _canvas.RemoveComponent(_componentViewModel);
    }

    public void Undo()
    {
        // Re-add the component (creates a new VM - update our reference for redo).
        // Keep the PDK source: a VM recreated without it would read as "built-in"
        // and slip through single-process enforcement on later copy/paste (#570).
        _component.PhysicalX = _x;
        _component.PhysicalY = _y;
        _componentViewModel = _canvas.AddComponent(_component, _templateName, _templatePdkSource);
        if (_componentViewModel.LaserConfig != null)
        {
            if (_laserWavelengthNm.HasValue)
                _componentViewModel.LaserConfig.WavelengthNm = _laserWavelengthNm.Value;
            if (_laserInputPower.HasValue)
                _componentViewModel.LaserConfig.InputPower = _laserInputPower.Value;
        }

        // Re-add connections. Dissolution may already have restored a survivor
        // original — both the manager and the VM list guard against duplicates.
        foreach (var connection in _deletedConnections)
        {
            _canvas.ConnectionManager.AddExistingConnection(connection);
            if (!_canvas.Connections.Any(c => c.Connection == connection))
                _canvas.Connections.Add(new WaveguideConnectionViewModel(connection));
        }

        // Recalculate routes asynchronously (grid is rebuilt inside RecalculateRoutesAsync)
        _ = _canvas.RecalculateRoutesAsync();

        // Invalidate simulation so power flow overlay updates with restored circuit
        _canvas.InvalidateSimulation();
    }

    /// <summary>
    /// Checks if a PhysicalPin is connected to a component.
    /// For ComponentGroups, also checks if the pin belongs to a GroupPin's InternalPin.
    /// </summary>
    private static bool IsPinConnectedToComponent(PhysicalPin pin, Component component)
    {
        // Direct connection to component's physical pin
        if (pin.ParentComponent == component)
            return true;

        // Check if component is a ComponentGroup and pin is from a GroupPin's InternalPin
        if (component is ComponentGroup group)
        {
            // Check if any ExternalPin's InternalPin matches this pin
            foreach (var externalPin in group.ExternalPins)
            {
                if (externalPin.InternalPin == pin)
                    return true;
            }
        }

        return false;
    }
}
