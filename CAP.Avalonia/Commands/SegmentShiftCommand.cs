using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Routing.InterconnectRouting.SegmentShift;

namespace CAP.Avalonia.Commands;

/// <summary>
/// Undoable parallel shift of a straight waveguide segment, produced by an in-canvas
/// midpoint-handle drag (issue #791). The whole drag is one command: <see cref="Execute"/>
/// applies the final cumulative offset, <see cref="Undo"/> restores the offset captured when
/// the drag began, so Ctrl+Z reverts the edit exactly. Offsets are cumulative per segment,
/// making re-application after the live drag a no-op.
/// </summary>
public sealed class SegmentShiftCommand : IUndoableCommand
{
    private readonly WaveguideConnectionViewModel _connection;
    private readonly int _straightIndex;
    private readonly double _beforeOffset;
    private readonly double _afterOffset;
    private readonly Action? _afterApply;

    /// <summary>Initializes a new instance of <see cref="SegmentShiftCommand"/>.</summary>
    /// <param name="connection">The connection whose segment was shifted.</param>
    /// <param name="straightIndex">0-based index of the segment among the path's straights.</param>
    /// <param name="beforeOffset">Cumulative offset (µm) before the drag, restored on undo.</param>
    /// <param name="afterOffset">Cumulative offset (µm) at the end of the drag.</param>
    /// <param name="afterApply">Optional callback run after each apply
    /// (collision re-check / repaint).</param>
    public SegmentShiftCommand(WaveguideConnectionViewModel connection, int straightIndex,
                               double beforeOffset, double afterOffset, Action? afterApply = null)
    {
        _connection = connection;
        _straightIndex = straightIndex;
        _beforeOffset = beforeOffset;
        _afterOffset = afterOffset;
        _afterApply = afterApply;
    }

    /// <inheritdoc/>
    public string Description => $"Shift segment {_straightIndex + 1}";

    /// <inheritdoc/>
    public void Execute() => ApplyOffset(_afterOffset);

    /// <inheritdoc/>
    public void Undo() => ApplyOffset(_beforeOffset);

    private void ApplyOffset(double offset)
    {
        // The route may have been rebuilt since this command was recorded (style change or
        // endpoint move clears the offsets and can shift segment indices) — then the apply
        // legitimately fails. Degrade to a no-op instead of pretending geometry changed.
        if (!SegmentShiftEditor.TryApplyShift(_connection.Connection, _straightIndex, offset, out _))
            return;
        _connection.NotifyPathChanged();
        _afterApply?.Invoke();
    }
}
