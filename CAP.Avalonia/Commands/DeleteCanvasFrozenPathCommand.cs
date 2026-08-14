using CAP.Avalonia.ViewModels.Canvas;

namespace CAP.Avalonia.Commands;

/// <summary>
/// Deletes a canvas-level pin-less frozen path (issue #856). Undo re-adds the SAME
/// view-model instance, preserving object identity across undo/redo cycles.
/// </summary>
public class DeleteCanvasFrozenPathCommand : IUndoableCommand
{
    private readonly DesignCanvasViewModel _canvas;
    private readonly CanvasFrozenPathViewModel _pathViewModel;

    /// <summary>Creates a delete command for the given canvas frozen path.</summary>
    public DeleteCanvasFrozenPathCommand(DesignCanvasViewModel canvas, CanvasFrozenPathViewModel pathViewModel)
    {
        _canvas = canvas;
        _pathViewModel = pathViewModel;
    }

    /// <inheritdoc />
    public string Description =>
        Services.Localization.LocalizationService.Instance.Translate("Command.DeleteFrozenPath");

    /// <inheritdoc />
    public void Execute()
    {
        _pathViewModel.IsSelected = false;
        _canvas.CanvasFrozenPaths.Remove(_pathViewModel);
    }

    /// <inheritdoc />
    public void Undo()
    {
        if (!_canvas.CanvasFrozenPaths.Contains(_pathViewModel))
            _canvas.CanvasFrozenPaths.Add(_pathViewModel);
    }
}
