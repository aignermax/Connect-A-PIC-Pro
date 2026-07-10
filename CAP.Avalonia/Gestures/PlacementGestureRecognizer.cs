using Avalonia;
using Avalonia.Input;
using CAP.Avalonia.Controls;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Panels;

namespace CAP.Avalonia.Gestures;

/// <summary>
/// Handles one-click placement in PlaceComponent, PlaceGroupTemplate, Delete, and Probe interaction modes.
/// Also updates placement preview overlays during pointer movement.
/// </summary>
public class PlacementGestureRecognizer : IGestureRecognizer
{
    private readonly CanvasInteractionState _state;
    private readonly Action _invalidate;

    /// <summary>Initializes a new instance of <see cref="PlacementGestureRecognizer"/>.</summary>
    public PlacementGestureRecognizer(CanvasInteractionState state, Action invalidate)
    {
        _state = state;
        _invalidate = invalidate;
    }

    /// <inheritdoc/>
    public bool TryRecognize(PointerPressedEventArgs e, Point canvasPoint, DesignCanvasViewModel canvas, MainViewModel? mainVm)
    {
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return false;
        var mode = mainVm?.CanvasInteraction.CurrentMode;

        if (mode == InteractionMode.PlaceComponent || mode == InteractionMode.PlaceGroupTemplate)
        {
            var (px, py) = canvas.GridSnap.Snap(canvasPoint.X, canvasPoint.Y);
            mainVm!.CanvasClicked(px, py);
            _invalidate();
            return true;
        }

        if (mode == InteractionMode.Delete || mode == InteractionMode.Probe)
        {
            mainVm!.CanvasClicked(canvasPoint.X, canvasPoint.Y);
            _invalidate();
            return true;
        }

        return false;
    }

    /// <inheritdoc/>
    public void UpdatePassiveState(Point canvasPoint, DesignCanvasViewModel canvas, MainViewModel? mainVm)
    {
        var mode = mainVm?.CanvasInteraction.CurrentMode;

        if (mode == InteractionMode.PlaceComponent && mainVm?.CanvasInteraction.SelectedTemplate != null)
        {
            _state.ShowPlacementPreview = true;
            _state.PlacementPreviewTemplate = mainVm.CanvasInteraction.SelectedTemplate;
            var (sx, sy) = canvas.GridSnap.Snap(canvasPoint.X, canvasPoint.Y);
            _state.PlacementPreviewPosition = new Point(sx, sy);
            _invalidate();
        }
        else if (_state.ShowPlacementPreview)
        {
            _state.ShowPlacementPreview = false;
            _invalidate();
        }

        if (mode == InteractionMode.PlaceGroupTemplate && mainVm?.CanvasInteraction.SelectedGroupTemplate != null)
        {
            _state.ShowGroupTemplatePlacementPreview = true;
            _state.GroupTemplatePlacementPreview = mainVm.CanvasInteraction.SelectedGroupTemplate;
            var (sx, sy) = canvas.GridSnap.Snap(canvasPoint.X, canvasPoint.Y);
            _state.GroupTemplatePlacementPreviewPosition = new Point(sx, sy);
            _invalidate();
        }
        else if (_state.ShowGroupTemplatePlacementPreview)
        {
            _state.ShowGroupTemplatePlacementPreview = false;
            _invalidate();
        }
    }

    /// <inheritdoc/>
    public void OnPointerMoved(PointerEventArgs e, Point delta, Point canvasPoint, DesignCanvasViewModel canvas, MainViewModel? mainVm) { }

    /// <inheritdoc/>
    public void OnPointerReleased(PointerReleasedEventArgs e, DesignCanvasViewModel canvas, MainViewModel? mainVm) { }

    /// <inheritdoc/>
    public void Cancel()
    {
        _state.ResetPlacementPreview();
        _state.ResetGroupTemplatePlacementPreview();
        _invalidate();
    }
}
