using Avalonia;
using Avalonia.Input;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Controls;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Panels;

namespace CAP.Avalonia.Gestures;

/// <summary>
/// Handles rubber-band multi-select in Select mode when clicking on empty canvas space.
/// Also handles double-click on background to exit group edit mode.
/// </summary>
public class SelectionBoxGestureRecognizer : IGestureRecognizer
{
    /// <summary>Screen-space drag below which a press+release counts as a plain click.</summary>
    private const double ClickTolerancePixels = 4.0;

    private readonly CanvasInteractionState _state;
    private readonly Action _invalidate;
    private readonly Func<double> _getZoom;
    private DesignCanvasViewModel? _activeCanvas;

    /// <summary>Initializes a new instance of <see cref="SelectionBoxGestureRecognizer"/>.</summary>
    public SelectionBoxGestureRecognizer(CanvasInteractionState state, Action invalidate, Func<double> getZoom)
    {
        _state = state;
        _getZoom = getZoom;
        _invalidate = invalidate;
    }

    /// <inheritdoc/>
    public bool TryRecognize(PointerPressedEventArgs e, Point canvasPoint, DesignCanvasViewModel canvas, MainViewModel? mainVm)
    {
        if (mainVm?.CanvasInteraction.CurrentMode != InteractionMode.Select) return false;
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return false;

        // Double-click on background exits group edit mode
        if (canvas.IsInGroupEditMode && IsDoubleClick())
        {
            if (mainVm?.CommandManager != null && canvas.CurrentEditGroup != null)
                mainVm.CommandManager.ExecuteCommand(new ExitGroupEditModeCommand(canvas, canvas.CurrentEditGroup));
            else
                canvas.ExitGroupEditMode();
            mainVm?.LeftPanel.HierarchyPanel?.RebuildTree();
            UpdateClickTime();
            _invalidate();
            return true;
        }

        UpdateClickTime();
        _activeCanvas = canvas;
        canvas.Selection.IsBoxSelecting = true;
        canvas.Selection.BoxStartX = canvasPoint.X;
        canvas.Selection.BoxStartY = canvasPoint.Y;
        canvas.Selection.BoxEndX = canvasPoint.X;
        canvas.Selection.BoxEndY = canvasPoint.Y;
        _invalidate();
        return true;
    }

    /// <inheritdoc/>
    public void UpdatePassiveState(Point canvasPoint, DesignCanvasViewModel canvas, MainViewModel? mainVm) { }

    /// <inheritdoc/>
    public void OnPointerMoved(PointerEventArgs e, Point delta, Point canvasPoint, DesignCanvasViewModel canvas, MainViewModel? mainVm)
    {
        if (!canvas.Selection.IsBoxSelecting) return;
        canvas.Selection.BoxEndX = canvasPoint.X;
        canvas.Selection.BoxEndY = canvasPoint.Y;
        _invalidate();
    }

    /// <inheritdoc/>
    public void OnPointerReleased(PointerReleasedEventArgs e, DesignCanvasViewModel canvas, MainViewModel? mainVm)
    {
        if (!canvas.Selection.IsBoxSelecting) return;
        var (minX, minY, maxX, maxY) = canvas.Selection.GetNormalizedBox();
        canvas.Selection.IsBoxSelecting = false;
        _activeCanvas = null;

        // A (near-)zero-size box is a plain left-click, not a rubber-band. Route it through
        // the regular click pipeline (SelectAt): unlike SelectInRectangle — which only knows
        // components — that also selects a waveguide CONNECTION under the cursor, so a line
        // is selectable with the left button and not only via the right-click context path.
        double clickTolerance = ClickTolerancePixels / Math.Max(_getZoom(), 0.0001);
        if (maxX - minX < clickTolerance && maxY - minY < clickTolerance)
        {
            mainVm?.CanvasClicked(maxX, maxY);
            _invalidate();
            return;
        }

        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        canvas.Selection.SelectInRectangle(canvas.Components, minX, minY, maxX, maxY, addToSelection: ctrl, removeFromSelection: alt);
        _invalidate();
    }

    /// <inheritdoc/>
    public void Cancel()
    {
        if (_activeCanvas != null) _activeCanvas.Selection.IsBoxSelecting = false;
        _activeCanvas = null;
        _invalidate();
    }

    private bool IsDoubleClick()
    {
        var elapsed = (DateTime.UtcNow - _state.LastClickTime).TotalMilliseconds;
        return elapsed < CanvasInteractionState.DoubleClickMilliseconds && _state.LastClickedComponent == null;
    }

    private void UpdateClickTime()
    {
        _state.LastClickTime = DateTime.UtcNow;
        _state.LastClickedComponent = null;
    }
}
