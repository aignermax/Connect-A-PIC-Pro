using Avalonia;
using Avalonia.Media;
using CAP.Avalonia.Controls.Canvas.ComponentPreview;
using CAP.Avalonia.Controls.Rendering.LabelDeclutter;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.Components.Core;

namespace CAP.Avalonia.Controls.Rendering;

/// <summary>
/// Renders all components (simple and group) onto the design canvas.
/// Handles dimming in group-edit mode, lock icons, and group hierarchy display.
/// Items outside the viewport are culled and tiny on-screen items are drawn
/// body-only (see <see cref="RenderCulling"/>) so huge imported designs stay
/// responsive. Implements <see cref="ICanvasRenderer"/> for world-space rendering.
/// </summary>
public sealed class ComponentRenderer : ICanvasRenderer
{
    private readonly PinRenderer _pinRenderer = new();
    private readonly ComponentOutlineRenderer _outlineRenderer = new();
    private readonly ComponentNameLabelComputer _nameLabels = new();

    // Static readonly — never allocate per component per frame (see
    // ComponentOutlineRenderer). "Dimmed" variants carry the group-edit alpha 128.
    private static readonly IBrush SelectedFillBrush = new SolidColorBrush(Color.FromArgb(255, 60, 80, 120));
    private static readonly IBrush SelectedFillBrushDimmed = new SolidColorBrush(Color.FromArgb(128, 60, 80, 120));
    private static readonly IBrush BodyFillBrush = new SolidColorBrush(Color.FromArgb(255, 40, 50, 70));
    private static readonly IBrush BodyFillBrushDimmed = new SolidColorBrush(Color.FromArgb(128, 40, 50, 70));
    private static readonly Pen SelectedBorderPen = new(new SolidColorBrush(Color.FromArgb(255, 0, 255, 255)), 2);
    private static readonly Pen SelectedBorderPenDimmed = new(new SolidColorBrush(Color.FromArgb(128, 0, 255, 255)), 2);
    private static readonly Pen NeutralBorderPen = new(new SolidColorBrush(Color.FromArgb(255, 128, 128, 128)), 1);
    private static readonly Pen NeutralBorderPenDimmed = new(new SolidColorBrush(Color.FromArgb(128, 128, 128, 128)), 1);
    private static readonly IBrush ChildNameBrush = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
    private static readonly IBrush ChildNameBrushDimmed = new SolidColorBrush(Color.FromArgb(128, 255, 255, 255));
    private static readonly Typeface LabelTypeface = new("Arial");

    /// <inheritdoc/>
    public void Render(DrawingContext context, CanvasRenderContext rc)
    {
        var viewport = RenderCulling.ComputeViewportWorld(rc.ViewModel.PanX, rc.ViewModel.PanY, rc.Bounds, rc.Zoom);
        var cullRect = RenderCulling.InflateForCulling(viewport, rc.Zoom);
        Guid? hoveredComponentId = rc.InteractionState.HoveredComponent?.Component.Id;
        var visibleNameIds = _nameLabels.GetVisibleLabelIds(
            rc.ViewModel.Components, hoveredComponentId, viewport, rc.Zoom);

        foreach (var comp in rc.ViewModel.Components)
            DrawComponent(context, comp, rc, visibleNameIds, cullRect);
    }

    private void DrawComponent(DrawingContext context, ComponentViewModel comp, CanvasRenderContext rc,
        IReadOnlySet<Guid> visibleNameIds, Rect cullRect)
    {
        bool isDimmed = IsComponentDimmedInEditMode(comp, rc.ViewModel);

        if (comp.Component is ComponentGroup group)
        {
            DrawComponentGroup(context, group, comp.IsSelected, rc, cullRect, isDimmed);
            return;
        }

        var rect = new Rect(comp.X, comp.Y, comp.Width, comp.Height);
        if (!cullRect.Intersects(rect))
            return;

        bool hasOutlines = comp.Component.OutlinePolygons is { Count: > 0 };
        DrawComponentBody(context, comp, rc, rect, hasOutlines, isDimmed);

        if (comp.IsSelected || !hasOutlines)
        {
            var borderPen = comp.IsSelected
                ? (isDimmed ? SelectedBorderPenDimmed : SelectedBorderPen)
                : (isDimmed ? NeutralBorderPenDimmed : NeutralBorderPen);
            context.DrawRectangle(borderPen, rect);
        }

        // Below the LOD threshold, pins, labels and icons are sub-pixel noise —
        // the body drawn above is all that remains visible at that scale.
        if (RenderCulling.IsBelowLodThreshold(comp.Width, comp.Height, rc.Zoom))
            return;

        _pinRenderer.DrawComponentPins(context, comp, rc, isDimmed);

        // A name overlapping a higher-priority (selected > hovered > rest) label is skipped
        // rather than drawn as illegible overlapping text.
        if (visibleNameIds.Contains(comp.Component.Id)
            && _nameLabels.TryGetLabelText(comp.Component.Id) is { } labelText)
            _pinRenderer.DrawComponentName(rc.Labels, comp, labelText, isDimmed);

        if (comp.IsLocked)
            DrawLockIcon(context, comp);

        if (comp.IsLightSource)
        {
            LaserIndicatorRenderer.Draw(context, comp,
                rc.InteractionState.HoveredLaserIconComponent == comp,
                rc.ViewModel.IsSimulationModeActive);
        }
    }

    private void DrawComponentBody(DrawingContext context, ComponentViewModel comp, CanvasRenderContext rc,
        Rect rect, bool hasOutlines, bool isDimmed)
    {
        if (hasOutlines)
        {
            // GDS-imported component: draw its outline polygons instead of the plain
            // rectangle body. No Nazca preview is fetched for it — the real imported
            // geometry is already on screen, and the synthesized import function name
            // would only spawn a doomed Python render per unique cell.
            _outlineRenderer.Draw(context, comp, comp.Component.OutlinePolygons!, isDimmed, rc.Zoom);
            return;
        }

        var fillBrush = comp.IsSelected
            ? (isDimmed ? SelectedFillBrushDimmed : SelectedFillBrush)
            : (isDimmed ? BodyFillBrushDimmed : BodyFillBrush);
        context.FillRectangle(fillBrush, rect);

        var previewData = rc.GdsPreviewRenderService?.TryGetPreview(comp);
        if (previewData != null)
            GdsPolygonRenderer.DrawGdsPreview(context, previewData, comp);
    }

    private void DrawComponentGroup(DrawingContext context, ComponentGroup group, bool isSelected,
        CanvasRenderContext rc, Rect cullRect, bool isDimmed = false)
    {
        var vm = rc.ViewModel;
        bool isHovered = rc.InteractionState.HoveredGroup == group;
        bool isCurrentEditGroup = vm.CurrentEditGroup == group;

        var bounds = ComponentGroupRenderer.CalculateGroupBounds(group);
        if (group.OutlinePolygons is { Count: > 0 } backgroundPolygons && cullRect.Intersects(bounds))
        {
            // Imported background geometry (base plates, exclusion zones, logos)
            // draws beneath the children so components stay legible on top.
            _outlineRenderer.Draw(context, group.PhysicalX, group.PhysicalY,
                group.WidthMicrometers, group.HeightMicrometers,
                group.RotationDegrees, backgroundPolygons, isDimmed, rc.Zoom,
                group.UnrotatedWidthMicrometers, group.UnrotatedHeightMicrometers);
        }

        foreach (var child in group.ChildComponents)
        {
            if (child is ComponentGroup nestedGroup)
            {
                DrawComponentGroup(context, nestedGroup, isSelected, rc, cullRect, isDimmed);
                continue;
            }

            var childRect = new Rect(child.PhysicalX, child.PhysicalY, child.WidthMicrometers, child.HeightMicrometers);
            if (cullRect.Intersects(childRect))
                DrawGroupChild(context, child, childRect, rc, isHovered, isDimmed);
        }

        var powerFlowResult = vm.ShowPowerFlow ? vm.PowerFlowVisualizer.CurrentResult : null;
        var fadeThreshold = vm.PowerFlowVisualizer.FadeThresholdDb;
        foreach (var frozenPath in group.InternalPaths)
        {
            // Frozen paths may reach outside the group's child bounds, so each one
            // is culled individually by its cached bounding box.
            if (RenderCulling.GetFrozenPathBounds(frozenPath) is { } pathBounds && !cullRect.Intersects(pathBounds))
                continue;
            ComponentGroupRenderer.RenderFrozenWaveguidePath(context, frozenPath, powerFlowResult, fadeThreshold, cullRect);
        }

        if (!cullRect.Intersects(bounds))
            return;

        if (!isCurrentEditGroup)
        {
            if (isSelected)
                ComponentGroupRenderer.RenderGroupSelectionBorder(context, bounds, isDimmed);
            else
                ComponentGroupRenderer.RenderGroupBorder(context, bounds, isHovered, isDimmed);

            ComponentGroupRenderer.RenderGroupNameLabel(context, bounds, group.GroupName, isDimmed,
                rc.InteractionState.HoveredGroupLabel == group);
            ComponentGroupRenderer.RenderGroupLockIcon(context, group,
                rc.InteractionState.HoveredGroupLockIcon == group);
        }

        RenderGroupPins(context, group, isCurrentEditGroup, isHovered, vm, rc.Labels);
    }

    private void DrawGroupChild(DrawingContext context, Component child, Rect childRect,
        CanvasRenderContext rc, bool isGroupHovered, bool isDimmed)
    {
        if (isGroupHovered)
            ComponentGroupRenderer.RenderGroupHoverOverlay(context, childRect.X, childRect.Y, childRect.Width, childRect.Height);

        if (child.OutlinePolygons is { Count: > 0 } childOutlines)
        {
            // GDS-imported child: draw its outline polygons instead of the plain
            // rectangle body — the same branch DrawComponent takes for a top-level
            // outlined component (same renderer, same dimming). Grouped children
            // keep their thin bbox border below: inside a group the footprint
            // rectangle is the hover/selection affordance, so it stays visible
            // just like the selection border does for outlined top-level components.
            _outlineRenderer.Draw(context, child.PhysicalX, child.PhysicalY,
                child.WidthMicrometers, child.HeightMicrometers,
                child.RotationDegrees, childOutlines, isDimmed, rc.Zoom,
                child.UnrotatedWidthMicrometers, child.UnrotatedHeightMicrometers);
        }
        else
        {
            context.FillRectangle(isDimmed ? BodyFillBrushDimmed : BodyFillBrush, childRect);
        }
        context.DrawRectangle(isDimmed ? NeutralBorderPenDimmed : NeutralBorderPen, childRect);

        if (RenderCulling.IsBelowLodThreshold(childRect.Width, childRect.Height, rc.Zoom))
            return;

        var displayName = child.HumanReadableName ?? child.Identifier;
        // Deferred to the topmost label pass like every other name label: a top-level
        // component drawn after this group must never paint over a child name.
        var nameBrush = isDimmed ? ChildNameBrushDimmed : ChildNameBrush;
        rc.Labels.Enqueue(
            new FormattedText(displayName, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, LabelTypeface, 10, nameBrush),
            nameBrush,
            new Point(childRect.X + 3, childRect.Y + 3));
    }

    private static void RenderGroupPins(DrawingContext context, ComponentGroup group, bool isCurrentEditGroup, bool isHovered, DesignCanvasViewModel vm, DeferredLabelLayer labels)
    {
        if (!isCurrentEditGroup)
        {
            var allConnections = vm.Connections.Select(c => c.Connection);
            var unoccupiedPins = GroupPinOccupancyChecker.GetUnoccupiedPins(group, allConnections);
            var highlightedPin = vm.HighlightedPin?.Pin;
            foreach (var externalPin in unoccupiedPins)
            {
                bool isPinHovered = highlightedPin != null && externalPin.InternalPin == highlightedPin;
                ComponentGroupRenderer.RenderUnoccupiedGroupPin(context, externalPin, group, isPinHovered, labels);
            }
        }
        else
        {
            foreach (var externalPin in group.ExternalPins)
                ComponentGroupRenderer.RenderExternalPin(context, externalPin, group, isHovered, labels);
        }
    }

    private static void DrawLockIcon(DrawingContext context, ComponentViewModel comp)
    {
        double iconSize = Math.Clamp(Math.Min(comp.Width, comp.Height) * 0.25, 12, 24);
        double iconX = comp.X + comp.Width - iconSize - 4;
        double iconY = comp.Y + comp.Height - iconSize - 4;

        context.DrawEllipse(new SolidColorBrush(Color.FromArgb(180, 40, 40, 40)), null,
            new Point(iconX + iconSize / 2, iconY + iconSize / 2), iconSize / 2, iconSize / 2);

        double bodyWidth = iconSize * 0.5;
        double bodyHeight = iconSize * 0.5;
        double bodyX = iconX + (iconSize - bodyWidth) / 2;
        double bodyY = iconY + iconSize * 0.5;
        context.DrawRectangle(Brushes.Orange, null, new Rect(bodyX, bodyY, bodyWidth, bodyHeight));

        double shackleWidth = iconSize * 0.4;
        double shackleHeight = iconSize * 0.3;
        double shackleCenterX = iconX + iconSize / 2;
        var shackleGeometry = new StreamGeometry();
        using (var ctx = shackleGeometry.Open())
        {
            ctx.BeginFigure(new Point(shackleCenterX - shackleWidth / 2, bodyY), false);
            ctx.ArcTo(new Point(shackleCenterX + shackleWidth / 2, bodyY),
                new Size(shackleWidth / 2, shackleHeight), 0, false, SweepDirection.CounterClockwise);
        }
        context.DrawGeometry(null, new Pen(Brushes.Orange, 2), shackleGeometry);
    }

    private static bool IsComponentDimmedInEditMode(ComponentViewModel comp, DesignCanvasViewModel vm)
    {
        if (!vm.IsInGroupEditMode || vm.CurrentEditGroup == null)
            return false;
        return !vm.CurrentEditGroup.ChildComponents.Contains(comp.Component);
    }
}
