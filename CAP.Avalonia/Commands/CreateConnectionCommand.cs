using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Components.Connections;
using CAP_Core.Components.PinKinds;

namespace CAP.Avalonia.Commands;

/// <summary>
/// Command for creating a waveguide connection between two pins.
/// Tracks and restores any connections that were overwritten.
/// Cross-domain pairs (optical ↔ electrical) are rejected: <see cref="Execute"/> is a no-op
/// for them — a defensive backstop. The UI connect paths (drag gesture, click-to-connect)
/// pre-check <see cref="PinKindHelper.AreKindsCompatible"/> and do not issue this command for
/// an incompatible pair (so no empty undo entry is created); this guard only catches a caller
/// that skips that check.
/// </summary>
public class CreateConnectionCommand : IUndoableCommand
{
    private readonly DesignCanvasViewModel _canvas;
    private readonly PhysicalPin _startPin;
    private readonly PhysicalPin _endPin;
    private WaveguideConnection? _connection;
    private WaveguideConnectionViewModel? _connectionViewModel;

    // Connections that were removed when creating this connection. Crossing
    // sub-connections are stored as their pre-split ORIGINALS (#705): restoring a
    // sub whose crossing was dissolved would leave ghost pins on a dead component.
    private List<WaveguideConnection>? _removedConnections;

    public CreateConnectionCommand(
        DesignCanvasViewModel canvas,
        PhysicalPin startPin,
        PhysicalPin endPin)
    {
        _canvas = canvas;
        _startPin = startPin;
        _endPin = endPin;
    }

    public string Description => $"Connect {_startPin.Name} to {_endPin.Name}";

    /// <summary>
    /// True when both pins belong to the same signal domain and may be connected.
    /// </summary>
    public bool ArePinKindsCompatible => PinKindHelper.AreKindsCompatible(_startPin, _endPin);

    public void Execute()
    {
        // Optical ↔ electrical connections are physically meaningless — refuse to create them
        // regardless of which UI path (drag gesture, click-to-connect, …) issued the command.
        if (!ArePinKindsCompatible)
            return;

        if (_connection != null && _connectionViewModel != null)
        {
            // Redo: remove any restored connections first, then re-add the new connection.
            // RemoveConnectionDeferred dissolves a crossing if one was re-inserted meanwhile.
            if (_removedConnections != null)
            {
                foreach (var conn in _removedConnections)
                {
                    var vm = _canvas.Connections.FirstOrDefault(c => c.Connection == conn);
                    if (vm != null)
                        _canvas.Connections.Remove(vm);
                    _canvas.ConnectionManager.RemoveConnectionDeferred(conn);
                }
            }

            _canvas.ConnectionManager.AddExistingConnection(_connection);
            _canvas.Connections.Add(_connectionViewModel);
        }
        else
        {
            // First execution: track connections that will be removed
            _removedConnections = new List<WaveguideConnection>();
            var crossing = _canvas.ConnectionManager.CrossingInsertion;

            // Find connections on start pin
            var startConnections = _canvas.Connections
                .Where(c => c.Connection.StartPin == _startPin || c.Connection.EndPin == _startPin)
                .ToList();

            // Find connections on end pin
            var endConnections = _canvas.Connections
                .Where(c => c.Connection.StartPin == _endPin || c.Connection.EndPin == _endPin)
                .ToList();

            // Store all connections that will be removed, normalized to their
            // pre-split originals (two subs of the same crossing dedupe to one).
            foreach (var connVm in startConnections.Concat(endConnections).Distinct())
            {
                var original = crossing?.ResolveToOriginal(connVm.Connection) ?? connVm.Connection;
                if (!_removedConnections.Contains(original))
                    _removedConnections.Add(original);
            }

            // Create new connection (this will remove the old ones)
            _connectionViewModel = _canvas.ConnectPins(_startPin, _endPin);
            if (_connectionViewModel != null)
            {
                _connection = _connectionViewModel.Connection;
            }
        }
        // Trigger async re-routing so the UI doesn't block
        _ = _canvas.RecalculateRoutesAsync();
    }

    public void Undo()
    {
        if (_connection != null && _connectionViewModel != null)
        {
            // Remove the new connection. If the crossing pass split it, this
            // dissolves the crossing instead of removing a stale object (#705).
            _canvas.ConnectionManager.RemoveConnectionDeferred(_connection);
            _canvas.Connections.Remove(_connectionViewModel);

            // Restore any connections that were removed
            if (_removedConnections != null)
            {
                foreach (var conn in _removedConnections)
                {
                    _canvas.ConnectionManager.AddExistingConnection(conn);
                    if (!_canvas.Connections.Any(c => c.Connection == conn))
                        _canvas.Connections.Add(new WaveguideConnectionViewModel(conn));
                }
            }

            _canvas.InvalidateSimulation();
            _ = _canvas.RecalculateRoutesAsync();
        }
    }
}
