using Avalonia;
using Avalonia.Input;
using CAP.Avalonia.Commands;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Panels;

namespace CAP.Avalonia.Gestures;

/// <summary>
/// Selects and drags canvas-level pin-less frozen paths (issue #856) in Select mode.
/// Ranked after <see cref="ComponentDragGestureRecognizer"/> (components win when
/// overlapping) and before the selection box, so clicking imported route geometry
/// selects and moves it instead of starting a box selection.
/// </summary>
public class FrozenPathDragGestureRecognizer : IGestureRecognizer
{
    private readonly Action _invalidate;
    private readonly Func<double> _getZoom;

    private CanvasFrozenPathViewModel? _dragging;
    private double _totalDx;
    private double _totalDy;

    /// <summary>Threshold (µm) below which a released drag is treated as a plain click.</summary>
    private const double MinDragDistanceMicrometers = 0.001;

    /// <summary>Initializes a new instance of <see cref="FrozenPathDragGestureRecognizer"/>.</summary>
    public FrozenPathDragGestureRecognizer(Action invalidate, Func<double> getZoom)
    {
        _invalidate = invalidate;
        _getZoom = getZoom;
    }

    /// <inheritdoc/>
    public bool TryRecognize(PointerPressedEventArgs e, Point canvasPoint, DesignCanvasViewModel canvas, MainViewModel? mainVm)
    {
        if (mainVm?.CanvasInteraction.CurrentMode != InteractionMode.Select) return false;
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return false;

        var hit = mainVm.CanvasInteraction.FindCanvasFrozenPathAt(canvasPoint.X, canvasPoint.Y);
        if (hit == null) return false;

        // Routes through the normal click pipeline so components/connections keep
        // their deselect semantics and the status bar reports the selection.
        mainVm.CanvasClicked(canvasPoint.X, canvasPoint.Y);

        _dragging = hit;
        _totalDx = 0;
        _totalDy = 0;
        _invalidate();
        return true;
    }

    /// <inheritdoc/>
    public void UpdatePassiveState(Point canvasPoint, DesignCanvasViewModel canvas, MainViewModel? mainVm) { }

    /// <inheritdoc/>
    public void OnPointerMoved(PointerEventArgs e, Point delta, Point canvasPoint, DesignCanvasViewModel canvas, MainViewModel? mainVm)
    {
        if (_dragging == null) return;
        double dx = delta.X / _getZoom(), dy = delta.Y / _getZoom();
        _dragging.Path.TranslateBy(dx, dy);
        _totalDx += dx;
        _totalDy += dy;
        _invalidate();
    }

    /// <inheritdoc/>
    public void OnPointerReleased(PointerReleasedEventArgs e, DesignCanvasViewModel canvas, MainViewModel? mainVm)
    {
        if (_dragging == null) return;
        if (Math.Abs(_totalDx) > MinDragDistanceMicrometers || Math.Abs(_totalDy) > MinDragDistanceMicrometers)
        {
            // Live drag already moved the geometry; the command records the delta
            // for undo/redo (same pattern as GroupMoveCommand).
            mainVm?.CommandManager.ExecuteCommand(
                new MoveCanvasFrozenPathCommand(_dragging, _totalDx, _totalDy));
        }
        _dragging = null;
        _invalidate();
    }

    /// <inheritdoc/>
    public void Cancel()
    {
        // Revert an aborted drag so the geometry snaps back to where it started.
        if (_dragging != null && (_totalDx != 0 || _totalDy != 0))
            _dragging.Path.TranslateBy(-_totalDx, -_totalDy);
        _dragging = null;
        _totalDx = 0;
        _totalDy = 0;
        _invalidate();
    }
}
