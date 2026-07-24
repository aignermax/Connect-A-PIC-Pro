using Avalonia;
using CAP.Avalonia.Controls.Rendering;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components;
using CAP_Core.Components.Core;

namespace CAP.Avalonia.Controls;

/// <summary>
/// Provides hit testing functionality for canvas elements (components, pins, connections).
/// </summary>
public class DesignCanvasHitTesting
{
    private const double PinHitRadius = 15.0;
    private const double ConnectionHitTolerance = 10.0;

    /// <summary>
    /// Checks if a point is within a component group's label bounds.
    /// Returns the group if the label is hit, null otherwise.
    /// This should be checked BEFORE HitTestComponent for prioritized label interaction.
    /// </summary>
    public static ComponentGroup? HitTestGroupLabel(Point canvasPoint, DesignCanvasViewModel? vm)
    {
        if (vm == null) return null;

        // Check all groups from top to bottom
        for (int i = vm.Components.Count - 1; i >= 0; i--)
        {
            var comp = vm.Components[i];
            if (comp.Component is ComponentGroup group)
            {
                var labelBounds = ComponentGroupRenderer.CalculateLabelBounds(group);
                if (labelBounds.Contains(canvasPoint))
                {
                    return group;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Checks if a point is within a component group's lock icon bounds.
    /// Returns the group if the lock icon is hit, null otherwise.
    /// This should be checked BEFORE HitTestGroupLabel for highest priority interaction.
    /// </summary>
    public static ComponentGroup? HitTestGroupLockIcon(Point canvasPoint, DesignCanvasViewModel? vm)
    {
        if (vm == null) return null;

        // Check all groups from top to bottom
        for (int i = vm.Components.Count - 1; i >= 0; i--)
        {
            var comp = vm.Components[i];
            if (comp.Component is ComponentGroup group)
            {
                var lockIconBounds = ComponentGroupRenderer.CalculateLockIconBounds(group);
                if (lockIconBounds.Contains(canvasPoint))
                {
                    return group;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Checks if a point is within a light-source component's laser on/off icon (#690).
    /// Only icons that are currently visible are hit (enabled lasers always; disabled
    /// ones only while a simulation mode is active). Returns the component if hit.
    /// This should be checked BEFORE HitTestComponent for prioritized icon interaction.
    /// </summary>
    public static ComponentViewModel? HitTestLaserIcon(Point canvasPoint, DesignCanvasViewModel? vm)
    {
        if (vm == null) return null;

        for (int i = vm.Components.Count - 1; i >= 0; i--)
        {
            var comp = vm.Components[i];
            if (LaserIndicatorRenderer.IsIconVisible(comp, vm.IsSimulationModeActive)
                && LaserIndicatorRenderer.CalculateIconBounds(comp).Contains(canvasPoint))
                return comp;

            // Components are drawn bottom-up, so this loop walks topmost-first: once
            // the point lies inside this component's body, anything below is occluded
            // and must not receive a hidden laser toggle.
            if (new Rect(comp.X, comp.Y, comp.Width, comp.Height).Contains(canvasPoint))
                return null;
        }

        return null;
    }

    /// <summary>
    /// Finds the component at the given canvas point (topmost first).
    /// For ComponentGroups, checks if the point is within the group's bounding box.
    /// In group edit mode, only tests child components of the current edit group.
    /// </summary>
    public static ComponentViewModel? HitTestComponent(Point canvasPoint, DesignCanvasViewModel? vm)
    {
        if (vm == null) return null;

        // In group edit mode, only test child components of the current edit group
        if (vm.IsInGroupEditMode && vm.CurrentEditGroup != null)
        {
            return HitTestGroupChildren(canvasPoint, vm.CurrentEditGroup, vm);
        }

        // Normal mode: test all top-level components
        for (int i = vm.Components.Count - 1; i >= 0; i--)
        {
            var comp = vm.Components[i];

            // For ComponentGroups, check the group's calculated bounds
            if (comp.Component is ComponentGroup group)
            {
                var groupRect = CalculateGroupBounds(group);
                if (groupRect.Contains(canvasPoint))
                {
                    return comp;
                }
            }
            else
            {
                var rect = new Rect(comp.X, comp.Y, comp.Width, comp.Height);
                if (rect.Contains(canvasPoint))
                {
                    return comp;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Hit tests child components within a group (used in group edit mode).
    /// </summary>
    private static ComponentViewModel? HitTestGroupChildren(Point canvasPoint, ComponentGroup editGroup, DesignCanvasViewModel vm)
    {
        // Test children from topmost to bottom
        for (int i = editGroup.ChildComponents.Count - 1; i >= 0; i--)
        {
            var child = editGroup.ChildComponents[i];

            // For nested groups, check the group's calculated bounds
            if (child is ComponentGroup childGroup)
            {
                var groupRect = CalculateGroupBounds(childGroup);
                if (groupRect.Contains(canvasPoint))
                {
                    // Find the ComponentViewModel for this child
                    var childVm = vm.Components.FirstOrDefault(c => c.Component == child);
                    if (childVm == null)
                    {
                        // Child not in top-level collection, create temporary wrapper
                        childVm = new ComponentViewModel(child);
                    }
                    return childVm;
                }
            }
            else
            {
                var rect = new Rect(child.PhysicalX, child.PhysicalY, child.WidthMicrometers, child.HeightMicrometers);
                if (rect.Contains(canvasPoint))
                {
                    // Find the ComponentViewModel for this child
                    var childVm = vm.Components.FirstOrDefault(c => c.Component == child);
                    if (childVm == null)
                    {
                        // Child not in top-level collection, create temporary wrapper
                        childVm = new ComponentViewModel(child);
                    }
                    return childVm;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Calculates the bounding rectangle for a ComponentGroup based on its children.
    /// </summary>
    private static Rect CalculateGroupBounds(ComponentGroup group)
    {
        if (group.ChildComponents.Count == 0)
        {
            return new Rect(group.PhysicalX, group.PhysicalY, group.WidthMicrometers, group.HeightMicrometers);
        }

        double minX = group.ChildComponents.Min(c => c.PhysicalX);
        double minY = group.ChildComponents.Min(c => c.PhysicalY);
        double maxX = group.ChildComponents.Max(c => c.PhysicalX + c.WidthMicrometers);
        double maxY = group.ChildComponents.Max(c => c.PhysicalY + c.HeightMicrometers);

        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>
    /// Finds the nearest pin within hit radius of the given canvas point.
    /// In group edit mode, only tests pins of child components in the current edit group.
    /// Outside edit mode, also tests unoccupied group pins for external connections.
    /// </summary>
    public static PhysicalPin? HitTestPin(Point canvasPoint, DesignCanvasViewModel? vm)
    {
        if (vm == null) return null;

        PhysicalPin? nearest = null;
        double nearestDistance = double.MaxValue;

        // In group edit mode, only test pins of child components
        if (vm.IsInGroupEditMode && vm.CurrentEditGroup != null)
        {
            foreach (var child in vm.CurrentEditGroup.ChildComponents)
            {
                foreach (var pin in child.PhysicalPins)
                {
                    var (pinX, pinY) = pin.GetAbsolutePosition();
                    var distance = Math.Sqrt(Math.Pow(canvasPoint.X - pinX, 2) + Math.Pow(canvasPoint.Y - pinY, 2));
                    if (distance < nearestDistance && distance <= PinHitRadius)
                    {
                        nearest = pin;
                        nearestDistance = distance;
                    }
                }
            }
        }
        else
        {
            // Normal mode: test all top-level component pins
            foreach (var comp in vm.Components)
            {
                foreach (var pin in comp.Component.PhysicalPins)
                {
                    var (pinX, pinY) = pin.GetAbsolutePosition();
                    var distance = Math.Sqrt(Math.Pow(canvasPoint.X - pinX, 2) + Math.Pow(canvasPoint.Y - pinY, 2));
                    if (distance < nearestDistance && distance <= PinHitRadius)
                    {
                        nearest = pin;
                        nearestDistance = distance;
                    }
                }

                // Also test group pins (unoccupied external pins)
                if (comp.Component is ComponentGroup group)
                {
                    var groupPinHit = HitTestGroupPin(canvasPoint, group, vm.Connections.Select(c => c.Connection));
                    if (groupPinHit.Pin != null)
                    {
                        var distance = groupPinHit.Distance;
                        if (distance < nearestDistance && distance <= PinHitRadius)
                        {
                            nearest = groupPinHit.Pin.InternalPin;
                            nearestDistance = distance;
                        }
                    }
                }
            }
        }

        return nearest;
    }

    /// <summary>
    /// Hit tests group pins for a ComponentGroup.
    /// Returns the nearest unoccupied GroupPin within hit radius.
    /// </summary>
    /// <param name="canvasPoint">Point to test in canvas coordinates.</param>
    /// <param name="group">The component group.</param>
    /// <param name="allConnections">All waveguide connections in the design.</param>
    /// <returns>Tuple of (GroupPin, Distance) or (null, MaxValue) if no hit.</returns>
    public static (GroupPin? Pin, double Distance) HitTestGroupPin(
        Point canvasPoint,
        ComponentGroup group,
        IEnumerable<CAP_Core.Components.Connections.WaveguideConnection> allConnections)
    {
        if (group == null)
            return (null, double.MaxValue);

        GroupPin? nearestPin = null;
        double nearestDistance = double.MaxValue;

        // Get unoccupied pins
        var unoccupiedPins = CAP_Core.Components.ComponentHelpers.GroupPinOccupancyChecker
            .GetUnoccupiedPins(group, allConnections);

        foreach (var pin in unoccupiedPins)
        {
            var (pinX, pinY) = CAP_Core.Components.ComponentHelpers.GroupPinOccupancyChecker
                .GetAbsolutePosition(pin, group);

            var distance = Math.Sqrt(
                Math.Pow(canvasPoint.X - pinX, 2) +
                Math.Pow(canvasPoint.Y - pinY, 2));

            if (distance < nearestDistance)
            {
                nearestPin = pin;
                nearestDistance = distance;
            }
        }

        return (nearestPin, nearestDistance);
    }

    /// <summary>
    /// Finds the connection nearest to the given canvas point within tolerance.
    /// Hit-tests the ACTUAL routed path (its segments), not just the straight endpoint line,
    /// so a bent/L-shaped/styled route is picked up where it is actually drawn. Returns the
    /// closest connection within tolerance so overlapping paths resolve to the nearest one.
    /// Mirrors the selection hit test in <c>CanvasInteractionViewModel.FindConnectionAt</c>.
    /// </summary>
    public static WaveguideConnectionViewModel? HitTestConnection(Point canvasPoint, DesignCanvasViewModel? vm)
    {
        if (vm == null) return null;

        WaveguideConnectionViewModel? closest = null;
        double closestDistance = ConnectionHitTolerance;
        foreach (var conn in vm.Connections)
        {
            double distance = DistanceToConnectionPath(conn, canvasPoint.X, canvasPoint.Y);
            if (distance <= closestDistance)
            {
                closestDistance = distance;
                closest = conn;
            }
        }

        return closest;
    }

    /// <summary>
    /// Shortest distance from a canvas point to a connection's drawn path: the minimum over its
    /// routed segments (arcs approximated by their chord — fine at the 10 px tolerance), or the
    /// straight endpoint line when the connection has not been routed yet.
    /// </summary>
    private static double DistanceToConnectionPath(WaveguideConnectionViewModel conn, double x, double y)
    {
        var segments = conn.Connection.GetPathSegments();
        if (segments.Count == 0)
            return PointToSegmentDistance(x, y, conn.StartX, conn.StartY, conn.EndX, conn.EndY);

        double min = double.MaxValue;
        foreach (var seg in segments)
        {
            double d = PointToSegmentDistance(
                x, y, seg.StartPoint.X, seg.StartPoint.Y, seg.EndPoint.X, seg.EndPoint.Y);
            if (d < min) min = d;
        }
        return min;
    }

    /// <summary>
    /// Calculates the minimum distance from a point to a line segment.
    /// </summary>
    private static double PointToSegmentDistance(
        double px, double py,
        double x1, double y1,
        double x2, double y2)
    {
        var dx = x2 - x1;
        var dy = y2 - y1;
        var lengthSq = dx * dx + dy * dy;

        if (lengthSq < 0.0001)
            return Math.Sqrt((px - x1) * (px - x1) + (py - y1) * (py - y1));

        var t = Math.Max(0, Math.Min(1, ((px - x1) * dx + (py - y1) * dy) / lengthSq));
        var projX = x1 + t * dx;
        var projY = y1 + t * dy;

        return Math.Sqrt((px - projX) * (px - projX) + (py - projY) * (py - projY));
    }
}
