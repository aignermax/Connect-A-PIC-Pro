using Avalonia;
using Avalonia.Input;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Controls;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Canvas.CrossingInsertion;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Routing.CrossingInsertion.ManualCrossing;

namespace CAP.Avalonia.Gestures;

/// <summary>
/// Handles clicks in Cut mode (issue #798): hovering highlights the nearest
/// crossing-insertion candidate within a screen-constant radius, and a left click on a
/// highlighted candidate inserts a crossing component there via the undoable
/// <see cref="InsertManualCrossingCommand"/>. Clicks on empty canvas are consumed so
/// the tool stays active until the user leaves the mode (Escape / another mode button).
/// </summary>
public class CutToolGestureRecognizer : IGestureRecognizer
{
    private const double HitRadiusPx = 10.0;

    private readonly CanvasInteractionState _state;
    private readonly Action _invalidate;
    private readonly Func<double> _getZoom;

    /// <summary>Initializes a new instance of <see cref="CutToolGestureRecognizer"/>.</summary>
    public CutToolGestureRecognizer(CanvasInteractionState state, Action invalidate, Func<double> getZoom)
    {
        _state = state;
        _invalidate = invalidate;
        _getZoom = getZoom;
    }

    /// <inheritdoc/>
    public bool TryRecognize(PointerPressedEventArgs e, Point canvasPoint, DesignCanvasViewModel canvas, MainViewModel? mainVm)
    {
        if (mainVm?.CanvasInteraction.CurrentMode != InteractionMode.Cut) return false;
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return false;

        var candidate = HitTest(canvasPoint);
        if (candidate != null)
            InsertCrossing(candidate, canvas, mainVm);
        return true;
    }

    /// <inheritdoc/>
    public void UpdatePassiveState(Point canvasPoint, DesignCanvasViewModel canvas, MainViewModel? mainVm)
    {
        if (mainVm?.CanvasInteraction.CurrentMode != InteractionMode.Cut) return;

        var hovered = HitTest(canvasPoint);
        if (hovered == _state.HoveredCutCandidate) return;
        _state.HoveredCutCandidate = hovered;
        _invalidate();
    }

    /// <inheritdoc/>
    public void OnPointerMoved(PointerEventArgs e, Point delta, Point canvasPoint, DesignCanvasViewModel canvas, MainViewModel? mainVm)
    {
    }

    /// <inheritdoc/>
    public void OnPointerReleased(PointerReleasedEventArgs e, DesignCanvasViewModel canvas, MainViewModel? mainVm)
    {
    }

    /// <inheritdoc/>
    public void Cancel()
    {
        _state.HoveredCutCandidate = null;
        _invalidate();
    }

    private void InsertCrossing(ManualCrossingCandidate candidate, DesignCanvasViewModel canvas, MainViewModel mainVm)
    {
        var instance = CrossingComponentInstance.CreateFromTemplates(mainVm.LeftPanel.AllTemplates);
        if (instance == null)
        {
            mainVm.StatusText = LocalizationService.Instance.Translate("Status.CutNoCrossingCell");
            return;
        }

        mainVm.CommandManager.ExecuteCommand(new InsertManualCrossingCommand(canvas, candidate, instance));
        mainVm.StatusText = LocalizationService.Instance.Translate("Status.CutInserted");
        _state.HoveredCutCandidate = null;
        _invalidate();
    }

    /// <summary>Nearest candidate within a screen-constant click radius, or null.</summary>
    private ManualCrossingCandidate? HitTest(Point canvasPoint)
    {
        double zoom = _getZoom();
        double radius = HitRadiusPx / (zoom <= 0 ? 1.0 : zoom);

        ManualCrossingCandidate? best = null;
        double bestDistance = radius;
        foreach (var candidate in _state.CutCandidates)
        {
            double dx = candidate.IntersectionPoint.X - canvasPoint.X;
            double dy = candidate.IntersectionPoint.Y - canvasPoint.Y;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance <= bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }
        return best;
    }
}
