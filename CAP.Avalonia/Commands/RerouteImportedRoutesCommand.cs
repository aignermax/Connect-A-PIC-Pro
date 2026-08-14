using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Routing;

namespace CAP.Avalonia.Commands;

/// <summary>
/// Undoable re-route of imported (frozen) waveguide routes:
/// <see cref="Execute"/> unfreezes the targets and hands them to the live router
/// (one asynchronous routing pass for the whole batch), <see cref="Undo"/> restores
/// the exact frozen geometry that was imported, so Ctrl+Z brings the imported
/// routing back unchanged.
/// </summary>
public sealed class RerouteImportedRoutesCommand : IUndoableCommand
{
    private readonly DesignCanvasViewModel _canvas;
    private readonly IReadOnlyList<WaveguideConnectionViewModel> _targets;
    private readonly Dictionary<WaveguideConnectionViewModel, RoutedPath> _frozenPaths = new();

    /// <summary>Initializes the command for the given frozen imported connections.</summary>
    /// <param name="canvas">The design canvas hosting the connections.</param>
    /// <param name="targets">The eligible frozen connections to re-route.</param>
    public RerouteImportedRoutesCommand(
        DesignCanvasViewModel canvas, IReadOnlyList<WaveguideConnectionViewModel> targets)
    {
        _canvas = canvas;
        _targets = targets;
    }

    /// <inheritdoc/>
    public string Description => _targets.Count == 1
        ? "Re-route imported route"
        : $"Re-route {_targets.Count} imported routes";

    /// <inheritdoc/>
    public void Execute()
    {
        foreach (var target in _targets)
        {
            var connection = target.Connection;
            // Snapshot only on the first execution; redo reuses the original geometry.
            if (!_frozenPaths.ContainsKey(target) && connection.RoutedPath is { } path)
                _frozenPaths[target] = path.DeepCopy();

            connection.IsRouteFrozen = false;
            // Incremental routing keeps any route whose endpoints still match, so the
            // frozen geometry must be dropped explicitly for the router to replace it.
            connection.InvalidateRoute();
        }

        _canvas.InvalidateSimulation();
        _ = _canvas.RecalculateRoutesAsync();
    }

    /// <inheritdoc/>
    public void Undo()
    {
        foreach (var target in _targets)
        {
            if (!_frozenPaths.TryGetValue(target, out var frozenPath))
                continue;
            // A private copy per undo so a later redo/undo cycle cannot alias the
            // restored live path with the stored snapshot.
            target.Connection.RestoreCachedPath(frozenPath.DeepCopy());
            target.Connection.IsRouteFrozen = true;
            target.NotifyPathChanged();
        }

        _canvas.InvalidateSimulation();
        // The recalculation pass keeps the restored geometry (frozen + endpoints match)
        // and refreshes losses/repaint, mirroring the other connection commands.
        _ = _canvas.RecalculateRoutesAsync();
    }
}
