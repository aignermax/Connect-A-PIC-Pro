using Avalonia;
using Avalonia.Media;
using CAP.Avalonia.Controls.Canvas.ComponentPreview;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.Components.Core;

namespace CAP.Avalonia.Controls.Rendering;

/// <summary>
/// Renders all components (simple and group) onto the design canvas.
/// Handles dimming in group-edit mode, lock icons, and group hierarchy display.
/// Implements <see cref="ICanvasRenderer"/> for world-space rendering.
/// </summary>
public sealed class ComponentRenderer : ICanvasRenderer
{
    private readonly PinRenderer _pinRenderer = new();

    /// <inheritdoc/>
    public void Render(DrawingContext context, CanvasRenderContext rc)
    {
        foreach (var comp in rc.ViewModel.Components)
            DrawComponent(context, comp, rc);
    }

    private void DrawComponent(DrawingContext context, ComponentViewModel comp, CanvasRenderContext rc)
    {
        bool isDimmed = IsComponentDimmedInEditMode(comp, rc.ViewModel);

        if (comp.Component is ComponentGroup group)
        {
            DrawComponentGroup(context, group, comp.IsSelected, rc, isDimmed);
            return;
        }

        byte alpha = (byte)(isDimmed ? 128 : 255);
        var rect = new Rect(comp.X, comp.Y, comp.Width, comp.Height);

        var fillBrush = comp.IsSelected
            ? new SolidColorBrush(Color.FromArgb(alpha, 60, 80, 120))
            : new SolidColorBrush(Color.FromArgb(alpha, 40, 50, 70));
        context.FillRectangle(fillBrush, rect);

        var previewData = rc.GdsPreviewRenderService?.TryGetPreview(comp);
        if (previewData != null)
            GdsPolygonRenderer.DrawGdsPreview(context, previewData, comp);

        var borderPen = comp.IsSelected
            ? new Pen(new SolidColorBrush(Color.FromArgb(alpha, 0, 255, 255)), 2)
            : new Pen(new SolidColorBrush(Color.FromArgb(alpha, 128, 128, 128)), 1);
        context.DrawRectangle(borderPen, rect);

        _pinRenderer.DrawComponentPins(context, comp, rc, isDimmed);
        _pinRenderer.DrawComponentName(context, comp, isDimmed);

        if (comp.IsLocked)
            DrawLockIcon(context, comp);

        if (comp.IsLightSource)
        {
            LaserIndicatorRenderer.Draw(context, comp,
                rc.InteractionState.HoveredLaserIconComponent == comp,
                rc.ViewModel.IsSimulationModeActive);
        }
    }

    private void DrawComponentGroup(DrawingContext context, ComponentGroup group, bool isSelected, CanvasRenderContext rc, bool isDimmed = false)
    {
        var vm = rc.ViewModel;
        bool isHovered = rc.InteractionState.HoveredGroup == group;
        bool isLabelHovered = rc.InteractionState.HoveredGroupLabel == group;
        bool isCurrentEditGroup = vm.CurrentEditGroup == group;
        byte alpha = (byte)(isDimmed ? 128 : 255);

        foreach (var child in group.ChildComponents)
        {
            if (child is ComponentGroup nestedGroup)
            {
                DrawComponentGroup(context, nestedGroup, isSelected, rc, isDimmed);
                continue;
            }

            if (isHovered)
                ComponentGroupRenderer.RenderGroupHoverOverlay(context, child.PhysicalX, child.PhysicalY, child.WidthMicrometers, child.HeightMicrometers);

            context.FillRectangle(new SolidColorBrush(Color.FromArgb(alpha, 40, 50, 70)),
                new Rect(child.PhysicalX, child.PhysicalY, child.WidthMicrometers, child.HeightMicrometers));
            context.DrawRectangle(new Pen(new SolidColorBrush(Color.FromArgb(alpha, 128, 128, 128)), 1),
                new Rect(child.PhysicalX, child.PhysicalY, child.WidthMicrometers, child.HeightMicrometers));

            var displayName = child.HumanReadableName ?? child.Identifier;
            context.DrawText(
                new FormattedText(displayName, System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, new Typeface("Arial"), 10,
                    new SolidColorBrush(Color.FromArgb(alpha, 255, 255, 255))),
                new Point(child.PhysicalX + 3, child.PhysicalY + 3));
        }

        var powerFlowResult = vm.ShowPowerFlow ? vm.PowerFlowVisualizer.CurrentResult : null;
        var fadeThreshold = vm.PowerFlowVisualizer.FadeThresholdDb;
        foreach (var frozenPath in group.InternalPaths)
            ComponentGroupRenderer.RenderFrozenWaveguidePath(context, frozenPath, powerFlowResult, fadeThreshold);

        var bounds = ComponentGroupRenderer.CalculateGroupBounds(group);
        if (!isCurrentEditGroup)
        {
            if (isSelected)
                ComponentGroupRenderer.RenderGroupSelectionBorder(context, bounds, isDimmed);
            else
                ComponentGroupRenderer.RenderGroupBorder(context, bounds, isHovered, isDimmed);

            ComponentGroupRenderer.RenderGroupNameLabel(context, bounds, group.GroupName, isDimmed, isLabelHovered);
            bool isLockIconHovered = rc.InteractionState.HoveredGroupLockIcon == group;
            ComponentGroupRenderer.RenderGroupLockIcon(context, group, isLockIconHovered);
        }

        RenderGroupPins(context, group, isCurrentEditGroup, isHovered, vm);
    }

    private static void RenderGroupPins(DrawingContext context, ComponentGroup group, bool isCurrentEditGroup, bool isHovered, DesignCanvasViewModel vm)
    {
        if (!isCurrentEditGroup)
        {
            var allConnections = vm.Connections.Select(c => c.Connection);
            var unoccupiedPins = GroupPinOccupancyChecker.GetUnoccupiedPins(group, allConnections);
            var highlightedPin = vm.HighlightedPin?.Pin;
            foreach (var externalPin in unoccupiedPins)
            {
                bool isPinHovered = highlightedPin != null && externalPin.InternalPin == highlightedPin;
                ComponentGroupRenderer.RenderUnoccupiedGroupPin(context, externalPin, group, isPinHovered);
            }
        }
        else
        {
            foreach (var externalPin in group.ExternalPins)
                ComponentGroupRenderer.RenderExternalPin(context, externalPin, group, isHovered);
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
