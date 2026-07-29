using Avalonia;
using Avalonia.Input;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Controls;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Components.Core;

namespace CAP.Avalonia.Gestures;

/// <summary>
/// Handles component selection and dragging in Select mode.
/// Also maintains passive hover state for groups, lock icons, and power flow connections.
/// </summary>
public class ComponentDragGestureRecognizer : IGestureRecognizer
{
    private readonly CanvasInteractionState _state;
    private readonly Action _invalidate;
    private readonly Func<double> _getZoom;
    private readonly Action<Cursor> _setCursor;

    /// <summary>Initializes a new instance of <see cref="ComponentDragGestureRecognizer"/>.</summary>
    public ComponentDragGestureRecognizer(CanvasInteractionState state, Action invalidate, Func<double> getZoom, Action<Cursor> setCursor)
    {
        _state = state;
        _invalidate = invalidate;
        _getZoom = getZoom;
        _setCursor = setCursor;
    }

    /// <inheritdoc/>
    public bool TryRecognize(PointerPressedEventArgs e, Point canvasPoint, DesignCanvasViewModel canvas, MainViewModel? mainVm)
    {
        if (mainVm?.CanvasInteraction.CurrentMode != InteractionMode.Select) return false;
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return false;

        var laserComp = DesignCanvasHitTesting.HitTestLaserIcon(canvasPoint, canvas);
        if (laserComp?.LaserConfig != null)
        {
            // Single-click direct manipulation (#690): toggle this coupler's laser.
            // Runs as an undoable command like the group-lock icon below; repaint and
            // resimulation are triggered by the LaserConfig.PropertyChanged handler.
            mainVm.CommandManager.ExecuteCommand(new ToggleLaserCommand(laserComp));
            mainVm.StatusText = laserComp.LaserConfig.IsEnabled
                ? $"Laser ON — '{laserComp.Name}' is an input"
                : $"Laser OFF — '{laserComp.Name}' is an output (listen-only)";
            return true;
        }

        var lockGroup = DesignCanvasHitTesting.HitTestGroupLockIcon(canvasPoint, canvas);
        if (lockGroup != null)
        {
            var cmd = new ToggleGroupLockCommand(lockGroup);
            mainVm.CommandManager.ExecuteCommand(cmd);
            mainVm.StatusText = lockGroup.IsLocked ? $"Locked group '{lockGroup.GroupName}'" : $"Unlocked group '{lockGroup.GroupName}'";
            _invalidate();
            return true;
        }

        var labelGroup = DesignCanvasHitTesting.HitTestGroupLabel(canvasPoint, canvas);
        var hit = labelGroup != null
            ? canvas.Components.FirstOrDefault(c => c.Component == labelGroup)
            : DesignCanvasHitTesting.HitTestComponent(canvasPoint, canvas);

        _state.DraggingComponent = ResolveTarget(hit, canvas);

        if (_state.DraggingComponent == null) return false;

        if (IsDoubleClick(_state.DraggingComponent) && _state.DraggingComponent.Component is ComponentGroup group)
        {
            EnterGroupEditMode(canvas, mainVm, group);
            _state.DraggingComponent = null;
            _invalidate();
            return true;
        }

        StartDrag(e, canvasPoint, canvas, mainVm);
        return _state.DraggingComponent != null;
    }

    private ComponentViewModel? ResolveTarget(ComponentViewModel? hit, DesignCanvasViewModel canvas)
    {
        if (hit == null) return null;
        if (canvas.IsInGroupEditMode) return hit;
        if (hit.Component is ComponentGroup) return hit;
        if (hit.Component.ParentGroup == null) return hit;
        var top = GetTopLevelGroup(hit.Component);
        return canvas.Components.FirstOrDefault(c => c.Component == top);
    }

    private void EnterGroupEditMode(DesignCanvasViewModel canvas, MainViewModel mainVm, ComponentGroup group)
    {
        if (mainVm.CommandManager != null)
            mainVm.CommandManager.ExecuteCommand(new EnterGroupEditModeCommand(canvas, group));
        else
            canvas.EnterGroupEditMode(group);
        mainVm.LeftPanel.HierarchyPanel?.RebuildTree();
    }

    private void StartDrag(PointerPressedEventArgs e, Point canvasPoint, DesignCanvasViewModel canvas, MainViewModel? mainVm)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        if (ctrl) { canvas.Selection.AddToSelection(_state.DraggingComponent!); _state.DraggingComponent = null; return; }
        if (alt) { canvas.Selection.RemoveFromSelection(_state.DraggingComponent!); _state.DraggingComponent = null; return; }

        if (!canvas.Selection.SelectedComponents.Contains(_state.DraggingComponent))
        {
            canvas.Selection.SelectSingle(_state.DraggingComponent!);
            canvas.SelectedComponent = _state.DraggingComponent;
        }

        mainVm?.CanvasClicked(canvasPoint.X, canvasPoint.Y);
        if (canvas.Selection.SelectedComponents.Count > 1) mainVm?.StartGroupMove(canvas.Selection.SelectedComponents);
        else mainVm?.StartMoveComponent(_state.DraggingComponent);

        _state.DragStartX = _state.DraggingComponent!.X;
        _state.DragStartY = _state.DraggingComponent.Y;
        _state.GroupDragStartPositions.Clear();
        foreach (var c in canvas.Selection.SelectedComponents) _state.GroupDragStartPositions[c] = (c.X, c.Y);
        _invalidate();
    }

    /// <inheritdoc/>
    public void UpdatePassiveState(Point canvasPoint, DesignCanvasViewModel canvas, MainViewModel? mainVm)
    {
        UpdateGroupHover(canvasPoint, canvas);
        UpdateLaserIconHover(canvasPoint, canvas);
        // Connection hover (highlight + power-flow label) is owned by HoverHighlightGestureRecognizer.
    }

    /// <summary>Tracks hover over a laser on/off icon (#690) for highlight + hand cursor.</summary>
    private void UpdateLaserIconHover(Point canvasPoint, DesignCanvasViewModel canvas)
    {
        var prev = _state.HoveredLaserIconComponent;
        var comp = DesignCanvasHitTesting.HitTestLaserIcon(canvasPoint, canvas);
        _state.HoveredLaserIconComponent = comp;
        if (comp != null) _setCursor(new Cursor(StandardCursorType.Hand));
        if (comp != prev) _invalidate();
    }

    private void UpdateGroupHover(Point canvasPoint, DesignCanvasViewModel canvas)
    {
        var prevGroup = _state.HoveredGroup;
        var prevLabel = _state.HoveredGroupLabel;
        var prevLock = _state.HoveredGroupLockIcon;
        // Compare by Component.Id, not object reference: in group-edit mode,
        // DesignCanvasHitTesting.HitTestGroupChildren fabricates a brand-new ComponentViewModel
        // wrapper on every call for a child that isn't in the top-level Components list, so a
        // reference comparison here would see a "change" (and force an invalidate/repaint) on
        // every single pointer move even while hovering the exact same component.
        var prevComponentId = _state.HoveredComponent?.Component.Id;

        var lockIcon = DesignCanvasHitTesting.HitTestGroupLockIcon(canvasPoint, canvas);
        _state.HoveredGroupLockIcon = lockIcon;

        if (lockIcon != null)
        {
            _state.HoveredGroup = lockIcon;
            _state.HoveredGroupLabel = null;
            _state.HoveredComponent = null;
            _setCursor(new Cursor(StandardCursorType.Hand));
        }
        else
        {
            var label = DesignCanvasHitTesting.HitTestGroupLabel(canvasPoint, canvas);
            _state.HoveredGroupLabel = label;
            if (label != null)
            {
                _state.HoveredGroup = label;
                _state.HoveredComponent = null;
                _setCursor(new Cursor(StandardCursorType.Hand));
            }
            else
            {
                var comp = DesignCanvasHitTesting.HitTestComponent(canvasPoint, canvas);
                _state.HoveredGroup = comp?.Component.ParentGroup != null
                    ? GetTopLevelGroup(comp.Component)
                    : comp?.Component as ComponentGroup;
                // Only a plain (non-group) component claims name-label hover priority; a group
                // hit is tracked separately above and its label uses its own hover state.
                _state.HoveredComponent = comp?.Component is ComponentGroup ? null : comp;
                _setCursor(Cursor.Default);
            }
        }

        var newComponentId = _state.HoveredComponent?.Component.Id;
        if (_state.HoveredGroup != prevGroup || _state.HoveredGroupLabel != prevLabel
            || _state.HoveredGroupLockIcon != prevLock || newComponentId != prevComponentId)
            _invalidate();
    }

    /// <inheritdoc/>
    public void OnPointerMoved(PointerEventArgs e, Point delta, Point canvasPoint, DesignCanvasViewModel canvas, MainViewModel? mainVm)
    {
        if (_state.DraggingComponent == null) return;
        double dx = delta.X / _getZoom(), dy = delta.Y / _getZoom();
        bool isGroup = canvas.Selection.SelectedComponents.Count > 1;
        if (isGroup)
        {
            foreach (var c in canvas.Selection.SelectedComponents) canvas.MoveComponent(c, dx, dy);
            // Both dragged together = pure translation: shift internal waveguides with the
            // components so they follow the pointer instead of snapping back only on drop.
            canvas.TranslateInternalConnectionRoutes(canvas.Selection.SelectedComponents, dx, dy);
        }
        else canvas.MoveComponent(_state.DraggingComponent!, dx, dy);
        if (canvas.AlignmentGuide.IsEnabled) canvas.AlignmentGuide.UpdateAlignments(_state.DraggingComponent!, canvas.Components);
        UpdateDragPreview(canvas, isGroup);
        _invalidate();
    }

    private void UpdateDragPreview(DesignCanvasViewModel canvas, bool isGroup)
    {
        double cx = _state.DraggingComponent!.X + _state.DraggingComponent.Width / 2.0;
        double cy = _state.DraggingComponent.Y + _state.DraggingComponent.Height / 2.0;
        var (scx, scy) = canvas.GridSnap.Snap(cx, cy);
        double px = scx - _state.DraggingComponent.Width / 2.0;
        double py = scy - _state.DraggingComponent.Height / 2.0;
        _state.DragPreviewPosition = new Point(px, py);
        double sdx = px - _state.DraggingComponent.X, sdy = py - _state.DraggingComponent.Y;
        _state.DragPreviewValid = isGroup
            ? canvas.Selection.CanMoveGroup(canvas, sdx, sdy)
            : canvas.CanMoveComponentTo(_state.DraggingComponent, px, py);
        _state.ShowDragPreview = canvas.GridSnap.IsEnabled || !_state.DragPreviewValid;
    }

    /// <inheritdoc/>
    public void OnPointerReleased(PointerReleasedEventArgs e, DesignCanvasViewModel canvas, MainViewModel? mainVm)
    {
        if (_state.DraggingComponent == null) return;
        bool isGroup = canvas.Selection.SelectedComponents.Count > 1;
        var (fx, fy) = ComputeFinalPosition(canvas);
        double dx = fx - _state.DraggingComponent.X, dy = fy - _state.DraggingComponent.Y;
        bool ok = isGroup ? canvas.Selection.CanMoveGroup(canvas, dx, dy) : canvas.CanMoveComponentTo(_state.DraggingComponent, fx, fy);
        if (ok) ApplyMove(dx, dy, canvas, isGroup);
        else RevertDrag(canvas, isGroup, mainVm);
        _state.ShowDragPreview = false;
        canvas.AlignmentGuide.ClearAlignments();
        canvas.AlignmentGuide.ResetSnapState();
        if (isGroup) { mainVm?.EndGroupMove(canvas.Selection.SelectedComponents); foreach (var c in canvas.Selection.SelectedComponents) c.IsSelected = true; }
        else mainVm?.EndMoveComponent();
        _invalidate();
    }

    private (double x, double y) ComputeFinalPosition(DesignCanvasViewModel canvas)
    {
        double fx = _state.DraggingComponent!.X, fy = _state.DraggingComponent.Y;
        if (canvas.AlignmentGuide?.IsEnabled == true && canvas.AlignmentGuide.SnapEnabled)
        {
            var (sdx, sdy) = canvas.AlignmentGuide.CalculateSnapOnRelease(_state.DraggingComponent, canvas.Components);
            fx += sdx; fy += sdy;
        }
        if (canvas.GridSnap?.IsEnabled == true)
        {
            var (scx, scy) = canvas.GridSnap.Snap(fx + _state.DraggingComponent.Width / 2.0, fy + _state.DraggingComponent.Height / 2.0);
            fx = scx - _state.DraggingComponent.Width / 2.0; fy = scy - _state.DraggingComponent.Height / 2.0;
        }
        return (fx, fy);
    }

    private void ApplyMove(double dx, double dy, DesignCanvasViewModel canvas, bool isGroup)
    {
        if (Math.Abs(dx) < 0.001 && Math.Abs(dy) < 0.001) return;
        if (isGroup) foreach (var c in canvas.Selection.SelectedComponents) canvas.MoveComponent(c, dx, dy);
        else canvas.MoveComponent(_state.DraggingComponent!, dx, dy);
    }

    private void RevertDrag(DesignCanvasViewModel canvas, bool isGroup, MainViewModel? mainVm)
    {
        if (isGroup)
        {
            foreach (var c in canvas.Selection.SelectedComponents)
                if (_state.GroupDragStartPositions.TryGetValue(c, out var pos))
                { double rdx = pos.x - c.X, rdy = pos.y - c.Y; if (Math.Abs(rdx) > 0.001 || Math.Abs(rdy) > 0.001) canvas.MoveComponent(c, rdx, rdy); }
        }
        else
        {
            double rdx = _state.DragStartX - _state.DraggingComponent!.X;
            double rdy = _state.DragStartY - _state.DraggingComponent.Y;
            if (Math.Abs(rdx) > 0.001 || Math.Abs(rdy) > 0.001) canvas.MoveComponent(_state.DraggingComponent, rdx, rdy);
        }
        if (mainVm != null) mainVm.StatusText = "Cannot place here - overlaps with another component";
    }

    /// <inheritdoc/>
    public void Cancel()
    {
        _state.DraggingComponent = null;
        _state.ShowDragPreview = false;
        _state.GroupDragStartPositions.Clear();
        _invalidate();
    }

    private bool IsDoubleClick(ComponentViewModel? component)
    {
        var now = DateTime.UtcNow;
        bool isDouble = (now - _state.LastClickTime).TotalMilliseconds < CanvasInteractionState.DoubleClickMilliseconds
                        && _state.LastClickedComponent == component;
        _state.LastClickTime = now;
        _state.LastClickedComponent = component;
        return isDouble;
    }

    private static ComponentGroup GetTopLevelGroup(Component component)
    {
        var g = component.ParentGroup as ComponentGroup;
        while (g?.ParentGroup != null) g = g.ParentGroup as ComponentGroup;
        return g!;
    }
}
