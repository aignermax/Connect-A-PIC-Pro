using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Connections;

namespace CAP.Avalonia.Commands;

/// <summary>
/// Command for deleting a waveguide connection. Crossing-aware (#705): deleting a
/// crossing sub-connection dissolves the crossing (the manager restores the other
/// net unsplit), and undo restores the pre-split ORIGINAL connection — never a
/// sub-connection whose crossing component no longer exists.
/// </summary>
public class DeleteConnectionCommand : IUndoableCommand
{
    private readonly DesignCanvasViewModel _canvas;

    /// <summary>The connection to remove on Execute and restore on Undo.</summary>
    private WaveguideConnection _target;

    public DeleteConnectionCommand(DesignCanvasViewModel canvas, WaveguideConnectionViewModel connectionVm)
    {
        _canvas = canvas;
        _target = connectionVm.Connection;
    }

    public string Description => "Delete connection";

    public void Execute()
    {
        // Don't delete locked connections
        if (_target.IsLocked)
            return;

        var crossing = _canvas.ConnectionManager.CrossingInsertion;
        var targetVm = _canvas.Connections.FirstOrDefault(c => c.Connection == _target);

        // Only act if the connection still exists somewhere (canvas VM, manager,
        // or as part of a crossing) — supports redo after intervening changes.
        bool exists = targetVm != null
            || _canvas.ConnectionManager.Connections.Contains(_target)
            || crossing?.IsCrossingConnection(_target) == true;
        if (!exists)
            return;

        // Undo must restore the pre-split original, not a crossing sub-connection.
        var restoreTarget = crossing?.ResolveToOriginal(_target) ?? _target;

        if (targetVm != null)
            _canvas.Connections.Remove(targetVm);

        // Dissolves the crossing when _target participates in one (the canvas
        // binder syncs the remaining connection view-models).
        _canvas.ConnectionManager.RemoveConnectionDeferred(_target);
        _target = restoreTarget;

        _ = _canvas.RecalculateRoutesAsync();
    }

    public void Undo()
    {
        _canvas.ConnectionManager.AddExistingConnection(_target);
        if (!_canvas.Connections.Any(c => c.Connection == _target))
            _canvas.Connections.Add(new WaveguideConnectionViewModel(_target));
        _ = _canvas.RecalculateRoutesAsync();
    }
}
