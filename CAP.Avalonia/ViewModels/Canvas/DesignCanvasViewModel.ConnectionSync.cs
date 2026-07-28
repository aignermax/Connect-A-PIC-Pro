using CAP_Core.Components.Connections;

namespace CAP.Avalonia.ViewModels.Canvas;

/// <summary>
/// Keeps <see cref="DesignCanvasViewModel.Connections"/> (the view-model collection bound to
/// the canvas) and <see cref="DesignCanvasViewModel.ConnectionManager"/> (the core connection
/// list) in sync when a command restores a connection it previously removed — undo of a
/// delete, or undo of a Cut-tool split. Idempotent: a no-op if a view-model already wraps the
/// connection, so undo/redo can call it freely without checking prior state.
/// </summary>
public partial class DesignCanvasViewModel
{
    /// <summary>
    /// Adds <paramref name="connection"/> back into the connection manager and, if no
    /// view-model already wraps it, creates one so the canvas renders it again.
    /// </summary>
    public void RestoreConnectionAndViewModel(WaveguideConnection connection)
    {
        ConnectionManager.AddExistingConnection(connection);
        if (!Connections.Any(c => c.Connection == connection))
            Connections.Add(new WaveguideConnectionViewModel(connection));
    }
}
