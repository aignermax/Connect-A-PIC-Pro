using Avalonia;
using Avalonia.Input;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Controls;
using CAP.Avalonia.Controls.Canvas.CutTool;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Canvas.CrossingInsertion;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Components.Connections;
using CAP_Core.Routing.CrossingInsertion.ManualCrossing;

namespace CAP.Avalonia.Gestures;

/// <summary>
/// Handles clicks in Cut mode: hovering highlights the nearest crossing-insertion candidate
/// within a screen-constant radius, falling back to a free cut projected onto the nearest
/// cuttable segment when no guide intersection is in range (see
/// <see cref="ManualCrossingCandidateFinder.ResolveCandidate"/>). A left click on a
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
    private readonly ManualCrossingCandidateFinder _finder = new();

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

        var candidate = ResolveCandidate(canvasPoint, canvas, mainVm);
        if (candidate != null)
            InsertCrossing(candidate, canvas, mainVm);
        return true;
    }

    /// <inheritdoc/>
    public void UpdatePassiveState(Point canvasPoint, DesignCanvasViewModel canvas, MainViewModel? mainVm)
    {
        if (mainVm?.CanvasInteraction.CurrentMode != InteractionMode.Cut) return;

        var hovered = ResolveCandidate(canvasPoint, canvas, mainVm);
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

    /// <summary>
    /// Inserts the crossing, but only after re-confirming the click will actually succeed:
    /// the candidate's connection must still be registered (a stale candidate — hit-tested
    /// against last frame's <see cref="CanvasInteractionState.CutCandidates"/> before a
    /// double-click's first insert was re-rendered away — must not stack a second crossing on
    /// the same spot), and the crossing template must still expose all four wired ports. Only
    /// a validated attempt reaches the undo stack and reports success; anything else is a
    /// silent no-op, exactly like clicking empty canvas.
    /// </summary>
    private void InsertCrossing(ManualCrossingCandidate candidate, DesignCanvasViewModel canvas, MainViewModel mainVm)
    {
        _state.ResetCutTool();

        if (!canvas.ConnectionManager.Connections.Contains(candidate.Connection))
        {
            _invalidate();
            return;
        }

        var template = CutToolCandidateComputer.FindUsableCrossingTemplate(mainVm.LeftPanel.AllTemplates);
        var instance = template == null ? null : CrossingComponentInstance.CreateFromTemplates(mainVm.LeftPanel.AllTemplates);
        if (instance == null)
        {
            mainVm.StatusText = LocalizationService.Instance.Translate("Status.CutNoCrossingCell");
            _invalidate();
            return;
        }

        mainVm.CommandManager.ExecuteCommand(
            new InsertManualCrossingCommand(canvas, candidate, instance, mainVm.ErrorConsole));
        mainVm.StatusText = LocalizationService.Instance.Translate("Status.CutInserted");
        _invalidate();
    }

    /// <summary>
    /// Resolves the candidate a click/hover at <paramref name="canvasPoint"/> would act on: a
    /// guide-intersection candidate within snap range takes precedence over a free cut, exactly
    /// as <see cref="ManualCrossingCandidateFinder.ResolveCandidate"/> defines. Both lookups
    /// share the same screen-constant radius, so the free-cut fallback feels like a natural
    /// extension of the same snap distance rather than a separate, looser hit area. The same
    /// grid-clearance check the ambient candidate sweep applies gates the free-cut fallback too,
    /// so a click cannot cut into a spot the sweep would never have offered as a candidate.
    /// </summary>
    private ManualCrossingCandidate? ResolveCandidate(
        Point canvasPoint, DesignCanvasViewModel canvas, MainViewModel? mainVm)
    {
        double zoom = _getZoom();
        double radius = HitRadiusPx / (zoom <= 0 ? 1.0 : zoom);
        var point = (canvasPoint.X, canvasPoint.Y);

        var template = mainVm == null
            ? null
            : CutToolCandidateComputer.FindUsableCrossingTemplate(mainVm.LeftPanel.AllTemplates);
        if (template == null)
            return _finder.ResolveCandidate(_state.CutCandidates, Array.Empty<WaveguideConnection>(), point, radius, 0);

        double requiredRun = CutToolCandidateComputer.ComputeRequiredRunMicrometers(template);
        var connections = CutToolCandidateComputer.CollectEligibleConnections(canvas);
        var footprint = CutToolCandidateComputer.BuildFootprintClearance(canvas.Router.PathfindingGrid, template);
        return _finder.ResolveCandidate(_state.CutCandidates, connections, point, radius, requiredRun, footprint);
    }
}
