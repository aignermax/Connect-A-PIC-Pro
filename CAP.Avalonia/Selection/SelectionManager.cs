using System.Collections.ObjectModel;
using CAP.Avalonia.ViewModels.Canvas;

namespace CAP.Avalonia.Selection;

/// <summary>
/// Manages multi-selection state for components on the design canvas.
/// Tracks selected components and provides operations on the selection group.
/// </summary>
public class SelectionManager
{
    /// <summary>
    /// Currently selected components. Observable for UI binding.
    /// </summary>
    public ObservableCollection<ComponentViewModel> SelectedComponents { get; } = new();

    /// <summary>
    /// Currently selected waveguide connections (issue #862): rubber-band selection collects
    /// optical connections here so the routing-style control can restyle them all at once.
    /// </summary>
    public ObservableCollection<WaveguideConnectionViewModel> SelectedConnections { get; } = new();

    /// <summary>
    /// Whether a box-selection drag is currently in progress.
    /// </summary>
    public bool IsBoxSelecting { get; set; }

    /// <summary>
    /// Start point of the box-selection rectangle in canvas coordinates.
    /// </summary>
    public double BoxStartX { get; set; }

    /// <summary>
    /// Start point of the box-selection rectangle in canvas coordinates.
    /// </summary>
    public double BoxStartY { get; set; }

    /// <summary>
    /// Current end point of the box-selection rectangle in canvas coordinates.
    /// </summary>
    public double BoxEndX { get; set; }

    /// <summary>
    /// Current end point of the box-selection rectangle in canvas coordinates.
    /// </summary>
    public double BoxEndY { get; set; }

    /// <summary>
    /// Whether there are multiple components selected.
    /// </summary>
    public bool HasMultipleSelected => SelectedComponents.Count > 1;

    /// <summary>
    /// Whether any components are selected.
    /// </summary>
    public bool HasSelection => SelectedComponents.Count > 0;

    /// <summary>
    /// Selects a single component, clearing previous selection.
    /// </summary>
    public void SelectSingle(ComponentViewModel component)
    {
        ClearSelection();
        component.IsSelected = true;
        SelectedComponents.Add(component);
    }

    /// <summary>
    /// Adds a component to the selection without removing others.
    /// </summary>
    public void AddToSelection(ComponentViewModel component)
    {
        if (!SelectedComponents.Contains(component))
        {
            SelectedComponents.Add(component);
            component.IsSelected = true;
        }
    }

    /// <summary>
    /// Removes a component from the selection.
    /// </summary>
    public void RemoveFromSelection(ComponentViewModel component)
    {
        if (SelectedComponents.Contains(component))
        {
            SelectedComponents.Remove(component);
            component.IsSelected = false;
        }
    }

    /// <summary>
    /// Toggles a component in the selection (Shift+click behavior).
    /// </summary>
    public void ToggleSelection(ComponentViewModel component)
    {
        if (SelectedComponents.Contains(component))
        {
            SelectedComponents.Remove(component);
            component.IsSelected = false;
        }
        else
        {
            SelectedComponents.Add(component);
            component.IsSelected = true;
        }
    }

    /// <summary>
    /// Clears all selected components.
    /// </summary>
    public void ClearSelection()
    {
        foreach (var comp in SelectedComponents)
        {
            comp.IsSelected = false;
        }
        SelectedComponents.Clear();
        ClearConnectionSelection();
    }

    /// <summary>
    /// Clears all selected connections without touching the component selection.
    /// </summary>
    public void ClearConnectionSelection()
    {
        foreach (var conn in SelectedConnections)
        {
            conn.IsSelected = false;
        }
        SelectedConnections.Clear();
    }

    /// <summary>
    /// Selects all components that intersect with the given rectangle.
    /// </summary>
    /// <param name="allComponents">All components on the canvas.</param>
    /// <param name="rectMinX">Left edge of selection rectangle.</param>
    /// <param name="rectMinY">Top edge of selection rectangle.</param>
    /// <param name="rectMaxX">Right edge of selection rectangle.</param>
    /// <param name="rectMaxY">Bottom edge of selection rectangle.</param>
    /// <param name="addToSelection">If true, adds to existing selection (Ctrl behavior).</param>
    /// <param name="removeFromSelection">If true, removes from existing selection (Alt behavior).</param>
    public void SelectInRectangle(
        IEnumerable<ComponentViewModel> allComponents,
        double rectMinX, double rectMinY,
        double rectMaxX, double rectMaxY,
        bool addToSelection = false,
        bool removeFromSelection = false)
    {
        if (!addToSelection && !removeFromSelection)
        {
            ClearSelection();
        }

        foreach (var comp in allComponents)
        {
            if (Intersects(comp, rectMinX, rectMinY, rectMaxX, rectMaxY))
            {
                if (removeFromSelection)
                {
                    // Alt: Remove from selection
                    if (SelectedComponents.Contains(comp))
                    {
                        SelectedComponents.Remove(comp);
                        comp.IsSelected = false;
                    }
                }
                else
                {
                    // Normal or Ctrl: Add to selection
                    if (!SelectedComponents.Contains(comp))
                    {
                        comp.IsSelected = true;
                        SelectedComponents.Add(comp);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Checks whether a component is fully inside the given rectangle.
    /// </summary>
    public static bool IsFullyInside(
        ComponentViewModel comp,
        double rectMinX, double rectMinY,
        double rectMaxX, double rectMaxY)
    {
        return comp.X >= rectMinX &&
               comp.Y >= rectMinY &&
               comp.X + comp.Width <= rectMaxX &&
               comp.Y + comp.Height <= rectMaxY;
    }

    /// <summary>
    /// Checks whether a component intersects (touches) the given rectangle.
    /// </summary>
    public static bool Intersects(
        ComponentViewModel comp,
        double rectMinX, double rectMinY,
        double rectMaxX, double rectMaxY)
    {
        // Check if rectangles overlap using standard AABB collision detection
        return comp.X < rectMaxX &&
               comp.X + comp.Width > rectMinX &&
               comp.Y < rectMaxY &&
               comp.Y + comp.Height > rectMinY;
    }

    /// <summary>
    /// Gets the normalized (min/max) box selection rectangle.
    /// Handles drag in any direction.
    /// </summary>
    public (double minX, double minY, double maxX, double maxY) GetNormalizedBox()
    {
        double minX = Math.Min(BoxStartX, BoxEndX);
        double minY = Math.Min(BoxStartY, BoxEndY);
        double maxX = Math.Max(BoxStartX, BoxEndX);
        double maxY = Math.Max(BoxStartY, BoxEndY);
        return (minX, minY, maxX, maxY);
    }

    /// <summary>
    /// Checks if a component is part of the current selection.
    /// </summary>
    public bool IsSelected(ComponentViewModel component)
    {
        return SelectedComponents.Contains(component);
    }

    /// <summary>
    /// Checks whether moving all selected components by the given delta
    /// would cause any collision with non-selected components.
    /// </summary>
    /// <param name="canvas">The canvas ViewModel for collision checks.</param>
    /// <param name="deltaX">Horizontal offset in micrometers.</param>
    /// <param name="deltaY">Vertical offset in micrometers.</param>
    /// <returns>True if the group can be moved by the given delta.</returns>
    public bool CanMoveGroup(DesignCanvasViewModel canvas, double deltaX, double deltaY)
    {
        foreach (var comp in SelectedComponents)
        {
            double newX = comp.X + deltaX;
            double newY = comp.Y + deltaY;

            if (!CanPlaceInGroup(canvas, comp, newX, newY))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Checks placement for a single component, excluding all selected components
    /// from collision checks (since they all move together).
    /// For ComponentGroups, uses child component and frozen path collision detection.
    /// </summary>
    private bool CanPlaceInGroup(
        DesignCanvasViewModel canvas,
        ComponentViewModel component,
        double x, double y)
    {
        const double MinGap = 5.0;

        // Check chip boundaries
        if (x < canvas.ChipMinX || y < canvas.ChipMinY ||
            x + component.Width > canvas.ChipMaxX ||
            y + component.Height > canvas.ChipMaxY)
        {
            return false;
        }

        // For ComponentGroups, use specialized collision detection
        if (component.Component is CAP_Core.Components.Core.ComponentGroup group)
        {
            // Get all components as Component instances
            var allComponents = canvas.Components.Select(c => c.Component).ToList();

            // Build exclusion set (all selected components move together)
            var excludeSet = new HashSet<CAP_Core.Components.Core.Component>(
                SelectedComponents.Select(c => c.Component));

            // Use GroupCollisionDetector for precise collision detection
            var detector = new CAP_Core.Grid.GroupCollisionDetector();
            return detector.CanPlaceGroup(group, x, y, allComponents, excludeSet);
        }

        // Regular component - check bounding box overlap with non-selected components
        foreach (var other in canvas.Components)
        {
            if (other == component) continue;
            if (SelectedComponents.Contains(other)) continue;

            bool overlaps =
                x - MinGap < other.X + other.Width &&
                x + component.Width + MinGap > other.X &&
                y - MinGap < other.Y + other.Height &&
                y + component.Height + MinGap > other.Y;

            if (overlaps) return false;
        }

        return true;
    }
}
