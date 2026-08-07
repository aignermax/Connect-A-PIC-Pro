using CAP_Core.Components.Core;
using CAP_Core.Routing;

namespace CAP_Core.Grid;

/// <summary>
/// Detects collisions for ComponentGroups by checking individual child components
/// and frozen waveguide paths instead of using the group's bounding box.
/// This allows placing groups where only empty space between children would overlap,
/// while blocking placement when actual components or frozen paths would collide.
/// </summary>
public class GroupCollisionDetector
{
    /// <summary>
    /// Padding around waveguide paths for collision detection (typical waveguide width).
    /// </summary>
    private const double WaveguideWidthPadding = 2.0;

    /// <summary>
    /// Minimum gap between components (micrometers).
    /// </summary>
    private const double MinComponentGap = 5.0;

    /// <summary>
    /// Checks if a ComponentGroup can be placed at a new position without colliding
    /// with existing components. Checks individual child components and frozen paths,
    /// not the group's bounding box.
    /// </summary>
    /// <param name="group">The ComponentGroup being moved.</param>
    /// <param name="newX">New X position for the group.</param>
    /// <param name="newY">New Y position for the group.</param>
    /// <param name="allComponents">All components in the canvas (including the group being moved).</param>
    /// <param name="excludeFromCollision">Components to exclude from collision checks (e.g., the group itself and members of multi-selection).</param>
    /// <param name="preDragOverlapPartners">
    /// Optional grandfather map for re-dropping a dragged group onto deliberately
    /// stacked placements (e.g. an exact GDS import): keyed by a moved leaf child,
    /// the components it already overlapped — within the placement gap — when the
    /// drag began. Those pairs are excluded from the rejection; overlaps with any
    /// other component still reject.
    /// </param>
    /// <returns>True if placement is valid (no collisions), false otherwise.</returns>
    public bool CanPlaceGroup(
        ComponentGroup group,
        double newX,
        double newY,
        IEnumerable<Component> allComponents,
        HashSet<Component>? excludeFromCollision = null,
        IReadOnlyDictionary<Component, HashSet<Component>>? preDragOverlapPartners = null)
    {
        excludeFromCollision ??= new HashSet<Component>();

        // Calculate movement delta from current position
        double deltaX = newX - group.PhysicalX;
        double deltaY = newY - group.PhysicalY;

        // Build set of group members for fast exclusion checks
        var groupMembers = new HashSet<Component>(group.GetAllComponentsRecursive()) { group };

        // Check each child component for collision
        foreach (var child in group.ChildComponents)
        {
            double childNewX = child.PhysicalX + deltaX;
            double childNewY = child.PhysicalY + deltaY;

            if (!CanPlaceChildComponent(child, childNewX, childNewY, allComponents, groupMembers, excludeFromCollision, preDragOverlapPartners))
            {
                return false; // Child component would collide
            }
        }

        // Check each frozen path for collision
        foreach (var frozenPath in group.InternalPaths)
        {
            if (!CanPlaceFrozenPath(frozenPath, deltaX, deltaY, allComponents, groupMembers, excludeFromCollision))
            {
                return false; // Frozen path would collide
            }
        }

        return true; // No collisions - OK to place
    }

    /// <summary>
    /// Checks if a child component (or nested group) can be placed at the given position.
    /// Recursively checks children if the component is itself a ComponentGroup.
    /// </summary>
    private bool CanPlaceChildComponent(
        Component child,
        double childNewX,
        double childNewY,
        IEnumerable<Component> allComponents,
        HashSet<Component> groupMembers,
        HashSet<Component> excludeFromCollision,
        IReadOnlyDictionary<Component, HashSet<Component>>? preDragOverlapPartners)
    {
        // If child is a nested ComponentGroup, recursively check its children
        if (child is ComponentGroup nestedGroup)
        {
            double nestedDeltaX = childNewX - nestedGroup.PhysicalX;
            double nestedDeltaY = childNewY - nestedGroup.PhysicalY;

            foreach (var nestedChild in nestedGroup.ChildComponents)
            {
                double nestedChildNewX = nestedChild.PhysicalX + nestedDeltaX;
                double nestedChildNewY = nestedChild.PhysicalY + nestedDeltaY;

                if (!CanPlaceChildComponent(nestedChild, nestedChildNewX, nestedChildNewY, allComponents, groupMembers, excludeFromCollision, preDragOverlapPartners))
                {
                    return false;
                }
            }

            // Also check nested group's frozen paths
            foreach (var frozenPath in nestedGroup.InternalPaths)
            {
                if (!CanPlaceFrozenPath(frozenPath, nestedDeltaX, nestedDeltaY, allComponents, groupMembers, excludeFromCollision))
                {
                    return false;
                }
            }

            return true;
        }

        // Regular component - check bounding box collision
        var childBounds = new Rect(
            childNewX - MinComponentGap,
            childNewY - MinComponentGap,
            child.WidthMicrometers + MinComponentGap * 2,
            child.HeightMicrometers + MinComponentGap * 2);

        // Check collision with all components except group members and excluded components
        foreach (var existing in allComponents)
        {
            // Skip group members (don't check against self)
            if (groupMembers.Contains(existing)) continue;

            // Skip excluded components (e.g., other selected components in multi-select move)
            if (excludeFromCollision.Contains(existing)) continue;

            // Skip grandfathered pairs (already overlapped when the drag began)
            if (IsPreDragOverlapPartner(preDragOverlapPartners, child, existing)) continue;

            // Check collision based on existing component type
            if (existing is ComponentGroup otherGroup)
            {
                // Check against other group's children (not bounding box!)
                if (DoesCollideWithGroup(childBounds, otherGroup, child, preDragOverlapPartners))
                {
                    return false; // Collision with another group's children
                }
            }
            else
            {
                // Regular component - check bounding box
                var existingBounds = new Rect(
                    existing.PhysicalX,
                    existing.PhysicalY,
                    existing.WidthMicrometers,
                    existing.HeightMicrometers);

                if (RectsOverlap(childBounds, existingBounds))
                {
                    return false; // Collision with regular component
                }
            }
        }

        return true; // No collision
    }

    /// <summary>
    /// True when the pair (<paramref name="movedChild"/>, <paramref name="partner"/>)
    /// is grandfathered: the two already overlapped (within the placement gap) when
    /// the current drag began, so re-dropping onto that same overlap is allowed.
    /// </summary>
    private static bool IsPreDragOverlapPartner(
        IReadOnlyDictionary<Component, HashSet<Component>>? preDragOverlapPartners,
        Component movedChild,
        Component partner) =>
        preDragOverlapPartners is not null
        && preDragOverlapPartners.TryGetValue(movedChild, out var partners)
        && partners.Contains(partner);

    /// <summary>
    /// Checks if a rectangle collides with any child component of a group.
    /// Grandfathered pairs (<paramref name="movedChild"/> vs a child that already
    /// overlapped it when the drag began) are skipped.
    /// </summary>
    private bool DoesCollideWithGroup(
        Rect testRect,
        ComponentGroup group,
        Component movedChild,
        IReadOnlyDictionary<Component, HashSet<Component>>? preDragOverlapPartners)
    {
        // Check each child component (recursively for nested groups)
        foreach (var child in group.ChildComponents)
        {
            if (child is ComponentGroup nestedGroup)
            {
                if (DoesCollideWithGroup(testRect, nestedGroup, movedChild, preDragOverlapPartners))
                {
                    return true;
                }
            }
            else
            {
                if (IsPreDragOverlapPartner(preDragOverlapPartners, movedChild, child)) continue;

                var childBounds = new Rect(
                    child.PhysicalX,
                    child.PhysicalY,
                    child.WidthMicrometers,
                    child.HeightMicrometers);

                if (RectsOverlap(testRect, childBounds))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if a frozen waveguide path would collide with any components when translated.
    /// </summary>
    private bool CanPlaceFrozenPath(
        FrozenWaveguidePath path,
        double deltaX,
        double deltaY,
        IEnumerable<Component> allComponents,
        HashSet<Component> groupMembers,
        HashSet<Component> excludeFromCollision)
    {
        if (path?.Path?.Segments == null) return true;

        foreach (var segment in path.Path.Segments)
        {
            // Get segment bounds with translation applied
            var segmentBounds = GetTranslatedSegmentBounds(segment, deltaX, deltaY);

            // Check if translated segment overlaps with any component
            foreach (var existing in allComponents)
            {
                // Skip group members
                if (groupMembers.Contains(existing)) continue;

                // Skip excluded components
                if (excludeFromCollision.Contains(existing)) continue;

                // Check collision based on component type
                if (existing is ComponentGroup otherGroup)
                {
                    // Check against other group's children
                    if (DoesSegmentCollideWithGroup(segmentBounds, otherGroup))
                    {
                        return false;
                    }
                }
                else
                {
                    // Check against regular component bounds
                    var compBounds = new Rect(
                        existing.PhysicalX,
                        existing.PhysicalY,
                        existing.WidthMicrometers,
                        existing.HeightMicrometers);

                    if (RectsOverlap(segmentBounds, compBounds))
                    {
                        return false; // Frozen path collides with component
                    }
                }
            }
        }

        return true; // No collisions
    }

    /// <summary>
    /// Checks if a path segment (represented as bounding box) collides with any child of a group.
    /// </summary>
    private bool DoesSegmentCollideWithGroup(Rect segmentBounds, ComponentGroup group)
    {
        foreach (var child in group.ChildComponents)
        {
            if (child is ComponentGroup nestedGroup)
            {
                if (DoesSegmentCollideWithGroup(segmentBounds, nestedGroup))
                {
                    return true;
                }
            }
            else
            {
                var childBounds = new Rect(
                    child.PhysicalX,
                    child.PhysicalY,
                    child.WidthMicrometers,
                    child.HeightMicrometers);

                if (RectsOverlap(segmentBounds, childBounds))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the bounding rectangle for a path segment with translation applied.
    /// Includes padding for waveguide width.
    /// </summary>
    private Rect GetTranslatedSegmentBounds(PathSegment segment, double deltaX, double deltaY)
    {
        if (segment is StraightSegment straight)
        {
            double startX = straight.StartPoint.X + deltaX;
            double startY = straight.StartPoint.Y + deltaY;
            double endX = straight.EndPoint.X + deltaX;
            double endY = straight.EndPoint.Y + deltaY;

            double minX = Math.Min(startX, endX) - WaveguideWidthPadding;
            double minY = Math.Min(startY, endY) - WaveguideWidthPadding;
            double maxX = Math.Max(startX, endX) + WaveguideWidthPadding;
            double maxY = Math.Max(startY, endY) + WaveguideWidthPadding;

            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }
        else if (segment is BendSegment bend)
        {
            // For arcs, use conservative bounding box: center +/- radius + padding
            double centerX = bend.Center.X + deltaX;
            double centerY = bend.Center.Y + deltaY;
            double radius = bend.RadiusMicrometers + WaveguideWidthPadding;

            return new Rect(
                centerX - radius,
                centerY - radius,
                radius * 2,
                radius * 2);
        }

        // Unknown segment type - return zero-size rect
        return new Rect(0, 0, 0, 0);
    }

    /// <summary>
    /// Checks if two rectangles overlap (with or without padding).
    /// </summary>
    private bool RectsOverlap(Rect a, Rect b)
    {
        return a.X < b.X + b.Width &&
               a.X + a.Width > b.X &&
               a.Y < b.Y + b.Height &&
               a.Y + a.Height > b.Y;
    }

    /// <summary>
    /// Simple rectangle struct for collision detection.
    /// </summary>
    private readonly struct Rect
    {
        public double X { get; }
        public double Y { get; }
        public double Width { get; }
        public double Height { get; }

        public Rect(double x, double y, double width, double height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }
    }
}
