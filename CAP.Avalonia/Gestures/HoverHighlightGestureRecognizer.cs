using Avalonia;
using Avalonia.Input;
using CAP.Avalonia.Controls;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Canvas;

namespace CAP.Avalonia.Gestures;

/// <summary>
/// Passive-only recognizer that tracks which waveguide connection the cursor is hovering over,
/// so the renderer can highlight it (thicker/brighter) and the user discovers it is clickable.
/// It never becomes an active gesture: <see cref="TryRecognize"/> always returns false and the
/// active-gesture callbacks are no-ops. Uses the same path-accurate hit test and 10 px tolerance
/// as connection selection, so what highlights on hover is exactly what a click would select.
/// </summary>
public sealed class HoverHighlightGestureRecognizer : IGestureRecognizer
{
    private readonly CanvasInteractionState _state;
    private readonly Action _invalidate;

    /// <summary>Creates the recognizer bound to the shared interaction state and repaint callback.</summary>
    /// <param name="state">Shared canvas interaction state holding <see cref="CanvasInteractionState.HoveredConnection"/>.</param>
    /// <param name="invalidate">Requests a canvas repaint when the hovered connection changes.</param>
    public HoverHighlightGestureRecognizer(CanvasInteractionState state, Action invalidate)
    {
        _state = state;
        _invalidate = invalidate;
    }

    /// <inheritdoc/>
    public bool TryRecognize(PointerPressedEventArgs e, Point canvasPoint, DesignCanvasViewModel canvas, MainViewModel? mainVm)
        => false;

    /// <inheritdoc/>
    public void UpdatePassiveState(Point canvasPoint, DesignCanvasViewModel canvas, MainViewModel? mainVm)
    {
        var previous = _state.HoveredConnection;
        _state.HoveredConnection = DesignCanvasHitTesting.HitTestConnection(canvasPoint, canvas);
        if (!ReferenceEquals(_state.HoveredConnection, previous))
            _invalidate();
    }

    /// <inheritdoc/>
    public void OnPointerMoved(PointerEventArgs e, Point delta, Point canvasPoint, DesignCanvasViewModel canvas, MainViewModel? mainVm) { }

    /// <inheritdoc/>
    public void OnPointerReleased(PointerReleasedEventArgs e, DesignCanvasViewModel canvas, MainViewModel? mainVm) { }

    /// <inheritdoc/>
    public void Cancel() { }
}
