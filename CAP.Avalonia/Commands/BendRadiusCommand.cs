using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Routing.InterconnectRouting;

namespace CAP.Avalonia.Commands;

/// <summary>
/// Undoable change of a single bend's radius, produced by an in-canvas bend-handle drag
/// (issue #574 slice 1). The whole drag is one command: <see cref="Execute"/> applies the
/// final radius, <see cref="Undo"/> restores the radius captured when the drag began, so
/// Ctrl+Z reverts the edit exactly.
/// </summary>
public sealed class BendRadiusCommand : IUndoableCommand
{
    private readonly WaveguideConnectionViewModel _connection;
    private readonly int _bendIndex;
    private readonly double _beforeRadius;
    private readonly double _afterRadius;
    private readonly Action? _afterApply;

    /// <summary>Initializes a new instance of <see cref="BendRadiusCommand"/>.</summary>
    /// <param name="connection">The connection whose bend was edited.</param>
    /// <param name="bendIndex">0-based index of the edited bend.</param>
    /// <param name="beforeRadius">Radius (µm) before the drag, restored on undo.</param>
    /// <param name="afterRadius">Radius (µm) at the end of the drag.</param>
    /// <param name="afterApply">Optional callback run after each apply (panel sync / repaint).</param>
    public BendRadiusCommand(WaveguideConnectionViewModel connection, int bendIndex,
                             double beforeRadius, double afterRadius, Action? afterApply = null)
    {
        _connection = connection;
        _bendIndex = bendIndex;
        _beforeRadius = beforeRadius;
        _afterRadius = afterRadius;
        _afterApply = afterApply;
    }

    /// <inheritdoc/>
    public string Description => $"Set bend {_bendIndex + 1} radius";

    /// <inheritdoc/>
    public void Execute() => ApplyRadius(_afterRadius);

    /// <inheritdoc/>
    public void Undo() => ApplyRadius(_beforeRadius);

    private void ApplyRadius(double radius)
    {
        // The route may have been rebuilt since this command was recorded (style change or
        // endpoint move clears the overrides and can shift bend indices) — then the apply
        // legitimately fails. Degrade to a no-op instead of pretending geometry changed.
        if (!BendRadiusEditor.TryApplyOverride(_connection.Connection, _bendIndex, radius, out _))
            return;
        _connection.NotifyPathChanged();
        _afterApply?.Invoke();
    }
}
