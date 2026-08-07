using System.Collections.ObjectModel;
using CAP_Core.Components;
using CAP_Core.Components.Core;

namespace CAP.Avalonia.ViewModels.Canvas.Services;

/// <summary>
/// Handles component collision detection, placement validation, and movement logic.
/// </summary>
public class ComponentPlacementService
{
    private readonly ObservableCollection<ComponentViewModel> _components;
    private readonly ObservableCollection<WaveguideConnectionViewModel> _connections;

    /// <summary>
    /// Minimum gap between components in micrometers.
    /// </summary>
    private const double MinComponentGapMicrometers = 5.0;

    /// <summary>
    /// Overlap partners of the component being dragged, captured at drag start
    /// (<see cref="BeginDrag"/>): imported layouts can hold deliberate stacked
    /// placements (the GDS importer places exactly, bypassing the gap rule), so
    /// pairs that ALREADY overlapped — within the placement gap — when the drag
    /// began are grandfathered: re-dropping the dragged component where it
    /// overlaps those same partners is allowed, overlaps with any other
    /// component still reject. Keyed by the moved component (a moved group's
    /// leaf children); consulted only while <see cref="IsDragging"/>, cleared on
    /// <see cref="EndDrag"/>.
    /// </summary>
    private readonly Dictionary<Component, HashSet<Component>> _preDragOverlapPartners = new();

    /// <summary>
    /// Chip boundary in micrometers.
    /// </summary>
    public double ChipMinX { get; set; } = 0;
    public double ChipMinY { get; set; } = 0;
    public double ChipMaxX { get; set; } = 5000;
    public double ChipMaxY { get; set; } = 5000;

    /// <summary>
    /// Whether the user is currently dragging a component.
    /// </summary>
    public bool IsDragging { get; set; }

    /// <summary>
    /// Whether a command (undo/redo) is executing (skips collision checks).
    /// </summary>
    public bool IsExecutingCommand { get; set; }

    /// <summary>
    /// Begins a drag of <paramref name="component"/>: remembers which components
    /// it already overlaps (within the placement gap) so the drop check can
    /// grandfather exactly those pairs — see <see cref="_preDragOverlapPartners"/>.
    /// </summary>
    public void BeginDrag(Component component)
    {
        IsDragging = true;
        _preDragOverlapPartners.Clear();
        if (component is ComponentGroup group)
            CaptureGroupOverlapPartners(group);
        else
            CaptureComponentOverlapPartners(component);
    }

    /// <summary>Ends the drag and forgets the pre-drag overlap partners.</summary>
    public void EndDrag()
    {
        IsDragging = false;
        _preDragOverlapPartners.Clear();
    }

    /// <summary>
    /// Initializes the placement service.
    /// </summary>
    public ComponentPlacementService(
        ObservableCollection<ComponentViewModel> components,
        ObservableCollection<WaveguideConnectionViewModel> connections)
    {
        _components = components;
        _connections = connections;
    }

    /// <summary>
    /// Checks if a component can be moved to the given position.
    /// </summary>
    public bool CanMoveComponentTo(ComponentViewModel component, double x, double y)
    {
        if (component.Component is ComponentGroup group)
            return CanMoveGroupTo(group, x, y, excludeComponent: component);

        return CanPlaceComponent(x, y, component.Width, component.Height, excludeComponent: component);
    }

    /// <summary>
    /// Moves a component by the given delta, respecting collision rules.
    /// Returns true if the move succeeded.
    /// </summary>
    public bool MoveComponent(
        ComponentViewModel component,
        double deltaX,
        double deltaY,
        bool isInGroupEditMode,
        ComponentGroup? currentEditGroup,
        Action<ComponentGroup>? updateExternalPins,
        Func<Task>? recalculateRoutes)
    {
        if (component.Component.IsLocked)
            return false;

        if (component.Component is ComponentGroup group)
            return MoveComponentGroup(component, group, deltaX, deltaY,
                isInGroupEditMode, currentEditGroup, updateExternalPins, recalculateRoutes);

        double newX = component.X + deltaX;
        double newY = component.Y + deltaY;

        if (!IsDragging && !IsExecutingCommand &&
            !CanPlaceComponent(newX, newY, component.Width, component.Height, excludeComponent: component))
        {
            return false;
        }

        component.X = newX;
        component.Y = newY;
        component.Component.PhysicalX = newX;
        component.Component.PhysicalY = newY;

        if (isInGroupEditMode && currentEditGroup != null)
            updateExternalPins?.Invoke(currentEditGroup);

        if (IsDragging)
        {
            NotifyAffectedConnections(component);
        }
        else
        {
            _ = recalculateRoutes?.Invoke();
        }

        return true;
    }

    /// <summary>
    /// Translates the routed path of every connection whose BOTH endpoints sit on components
    /// in <paramref name="selection"/> by (<paramref name="deltaX"/>, <paramref name="deltaY"/>),
    /// so an internal waveguide follows a joint drag of both its components in real time.
    /// Connections with only one selected endpoint are left untouched — their geometry
    /// genuinely changes and is re-routed on drop.
    /// </summary>
    public void TranslateInternalConnectionRoutes(
        IEnumerable<ComponentViewModel> selection, double deltaX, double deltaY)
    {
        var selected = new HashSet<Component>(
            selection.Select(c => c.Component).Where(c => !c.IsLocked));
        foreach (var conn in _connections)
        {
            var connection = conn.Connection;
            if (connection.RoutedPath is not { Segments.Count: > 0 })
                continue;
            if (!selected.Contains(connection.StartPin.ParentComponent) ||
                !selected.Contains(connection.EndPin.ParentComponent))
                continue;

            connection.ReplaceRoutedPath(connection.RoutedPath.TranslatedCopy(deltaX, deltaY));
            conn.NotifyPathChanged();
        }
    }

    /// <summary>
    /// Checks if a component can be placed without overlapping others and within chip boundaries.
    /// Pairs that already overlapped when the current drag began are grandfathered
    /// (<see cref="_preDragOverlapPartners"/>); genuinely new overlaps reject.
    /// </summary>
    public bool CanPlaceComponent(double x, double y, double width, double height,
        ComponentViewModel? excludeComponent = null)
    {
        if (x < ChipMinX || y < ChipMinY ||
            x + width > ChipMaxX || y + height > ChipMaxY)
            return false;

        var testRect = GapInflatedRect(x, y, width, height);

        foreach (var comp in _components)
        {
            if (comp == excludeComponent) continue;
            if (IsDragging && excludeComponent is not null &&
                IsPreDragOverlapPartner(excludeComponent.Component, comp.Component))
                continue; // grandfathered: this pair already overlapped when the drag began.
            var compRect = new Rect(comp.X, comp.Y, comp.Width, comp.Height);
            if (RectsOverlap(testRect, compRect))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Finds a valid position near the requested position if the exact position overlaps.
    /// </summary>
    public (double x, double y)? FindValidPlacement(double x, double y, double width, double height)
    {
        x = Math.Max(ChipMinX, Math.Min(x, ChipMaxX - width));
        y = Math.Max(ChipMinY, Math.Min(y, ChipMaxY - height));

        if (CanPlaceComponent(x, y, width, height))
            return (x, y);

        double searchStep = 50;
        double maxRadius = Math.Max(ChipMaxX - ChipMinX, ChipMaxY - ChipMinY);

        for (double radius = searchStep; radius <= maxRadius; radius += searchStep)
        {
            for (double angle = 0; angle < 360; angle += 30)
            {
                double testX = x + radius * Math.Cos(angle * Math.PI / 180);
                double testY = y + radius * Math.Sin(angle * Math.PI / 180);
                if (CanPlaceComponent(testX, testY, width, height))
                    return (testX, testY);
            }
        }

        return null;
    }

    private bool CanMoveGroupTo(ComponentGroup group, double x, double y,
        ComponentViewModel? excludeComponent = null)
    {
        if (x < ChipMinX || y < ChipMinY ||
            x + group.WidthMicrometers > ChipMaxX ||
            y + group.HeightMicrometers > ChipMaxY)
            return false;

        var allComponents = _components.Select(c => c.Component).ToList();
        var excludeSet = new HashSet<Component>();
        if (excludeComponent != null)
            excludeSet.Add(excludeComponent.Component);

        var detector = new CAP_Core.Grid.GroupCollisionDetector();
        return detector.CanPlaceGroup(group, x, y, allComponents, excludeSet,
            IsDragging ? _preDragOverlapPartners : null);
    }

    private bool MoveComponentGroup(
        ComponentViewModel component,
        ComponentGroup group,
        double deltaX,
        double deltaY,
        bool isInGroupEditMode,
        ComponentGroup? currentEditGroup,
        Action<ComponentGroup>? updateExternalPins,
        Func<Task>? recalculateRoutes)
    {
        double newX = component.X + deltaX;
        double newY = component.Y + deltaY;

        if (!IsDragging && !IsExecutingCommand)
        {
            var groupBounds = CalculateGroupBounds(group);
            double testX = groupBounds.X + deltaX;
            double testY = groupBounds.Y + deltaY;
            if (!CanPlaceComponent(testX, testY, groupBounds.Width, groupBounds.Height, excludeComponent: component))
                return false;
        }

        component.X = newX;
        component.Y = newY;
        group.MoveGroup(deltaX, deltaY);

        if (isInGroupEditMode && currentEditGroup != null)
            updateExternalPins?.Invoke(currentEditGroup);

        if (IsDragging)
        {
            foreach (var externalPin in group.ExternalPins)
            {
                foreach (var conn in _connections)
                {
                    if (conn.Connection.StartPin == externalPin.InternalPin ||
                        conn.Connection.EndPin == externalPin.InternalPin)
                        conn.NotifyPathChanged();
                }
            }
        }
        else
        {
            _ = recalculateRoutes?.Invoke();
        }

        return true;
    }

    private static (double X, double Y, double Width, double Height) CalculateGroupBounds(ComponentGroup group)
    {
        if (group.ChildComponents.Count == 0)
            return (group.PhysicalX, group.PhysicalY, group.WidthMicrometers, group.HeightMicrometers);

        double minX = group.ChildComponents.Min(c => c.PhysicalX);
        double minY = group.ChildComponents.Min(c => c.PhysicalY);
        double maxX = group.ChildComponents.Max(c => c.PhysicalX + c.WidthMicrometers);
        double maxY = group.ChildComponents.Max(c => c.PhysicalY + c.HeightMicrometers);
        return (minX, minY, maxX - minX, maxY - minY);
    }

    private void NotifyAffectedConnections(ComponentViewModel component)
    {
        foreach (var conn in _connections)
        {
            if (conn.Connection.StartPin.ParentComponent == component.Component ||
                conn.Connection.EndPin.ParentComponent == component.Component)
                conn.NotifyPathChanged();
        }
    }

    /// <summary>
    /// Records every canvas component <paramref name="component"/> overlaps at
    /// drag start, using the same gap-inflated rectangle test as
    /// <see cref="CanPlaceComponent"/> so every pair that COULD reject a re-drop
    /// at the original position is grandfathered.
    /// </summary>
    private void CaptureComponentOverlapPartners(Component component)
    {
        var testRect = GapInflatedRect(
            component.PhysicalX, component.PhysicalY,
            component.WidthMicrometers, component.HeightMicrometers);
        var partners = new HashSet<Component>();
        foreach (var comp in _components)
        {
            if (comp.Component == component) continue;
            if (RectsOverlap(testRect, new Rect(comp.X, comp.Y, comp.Width, comp.Height)))
                partners.Add(comp.Component);
        }
        if (partners.Count > 0)
            _preDragOverlapPartners[component] = partners;
    }

    /// <summary>
    /// Records, per leaf child of the moved <paramref name="group"/>, the
    /// components it overlaps at drag start — at the granularity
    /// <see cref="CAP_Core.Grid.GroupCollisionDetector"/> checks: regular
    /// top-level components, and the leaf children of OTHER groups.
    /// </summary>
    private void CaptureGroupOverlapPartners(ComponentGroup group)
    {
        var members = new HashSet<Component>(group.GetAllComponentsRecursive()) { group };
        foreach (var leaf in group.GetAllComponentsRecursive())
        {
            if (leaf is ComponentGroup) continue;
            var testRect = GapInflatedRect(
                leaf.PhysicalX, leaf.PhysicalY, leaf.WidthMicrometers, leaf.HeightMicrometers);
            var partners = new HashSet<Component>();
            foreach (var comp in _components)
            {
                if (members.Contains(comp.Component)) continue;
                if (comp.Component is ComponentGroup otherGroup)
                {
                    foreach (var otherLeaf in otherGroup.GetAllComponentsRecursive())
                    {
                        if (otherLeaf is ComponentGroup) continue;
                        if (RectsOverlap(testRect, RectOf(otherLeaf)))
                            partners.Add(otherLeaf);
                    }
                }
                else if (RectsOverlap(testRect, RectOf(comp.Component)))
                {
                    partners.Add(comp.Component);
                }
            }
            if (partners.Count > 0)
                _preDragOverlapPartners[leaf] = partners;
        }
    }

    /// <summary>
    /// True when <paramref name="moved"/> and <paramref name="partner"/> already
    /// overlapped (within the placement gap) when the current drag began — a
    /// grandfathered pair the drop check must not reject.
    /// </summary>
    private bool IsPreDragOverlapPartner(Component moved, Component partner) =>
        _preDragOverlapPartners.TryGetValue(moved, out var partners) && partners.Contains(partner);

    private static Rect GapInflatedRect(double x, double y, double width, double height) =>
        new(x - MinComponentGapMicrometers, y - MinComponentGapMicrometers,
            width + MinComponentGapMicrometers * 2, height + MinComponentGapMicrometers * 2);

    private static Rect RectOf(Component component) =>
        new(component.PhysicalX, component.PhysicalY,
            component.WidthMicrometers, component.HeightMicrometers);

    private static bool RectsOverlap(Rect a, Rect b)
    {
        return a.X < b.X + b.Width &&
               a.X + a.Width > b.X &&
               a.Y < b.Y + b.Height &&
               a.Y + a.Height > b.Y;
    }

    private readonly struct Rect
    {
        public double X { get; }
        public double Y { get; }
        public double Width { get; }
        public double Height { get; }

        public Rect(double x, double y, double width, double height)
        {
            X = x; Y = y; Width = width; Height = height;
        }
    }
}
