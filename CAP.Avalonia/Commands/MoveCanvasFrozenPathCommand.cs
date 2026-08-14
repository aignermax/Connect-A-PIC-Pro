using CAP.Avalonia.ViewModels.Canvas;

namespace CAP.Avalonia.Commands;

/// <summary>
/// Records a completed drag of a canvas-level pin-less frozen path (issue #856).
/// Like <c>GroupMoveCommand</c>, the live drag has already translated the geometry,
/// so the first Execute is a no-op; Undo/Redo translate by the recorded delta.
/// </summary>
public class MoveCanvasFrozenPathCommand : IUndoableCommand
{
    private readonly CanvasFrozenPathViewModel _pathViewModel;
    private readonly double _deltaX;
    private readonly double _deltaY;
    private bool _hasExecutedOnce;

    /// <summary>Creates a move command for a drag that moved the path by the given delta (µm).</summary>
    public MoveCanvasFrozenPathCommand(CanvasFrozenPathViewModel pathViewModel, double deltaX, double deltaY)
    {
        _pathViewModel = pathViewModel;
        _deltaX = deltaX;
        _deltaY = deltaY;
    }

    /// <inheritdoc />
    public string Description =>
        Services.Localization.LocalizationService.Instance.Translate("Command.MoveFrozenPath");

    /// <inheritdoc />
    public void Execute()
    {
        if (!_hasExecutedOnce)
        {
            // Live drag already moved the geometry.
            _hasExecutedOnce = true;
            return;
        }
        _pathViewModel.Path.TranslateBy(_deltaX, _deltaY);
    }

    /// <inheritdoc />
    public void Undo() => _pathViewModel.Path.TranslateBy(-_deltaX, -_deltaY);
}
