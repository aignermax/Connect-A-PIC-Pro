using CAP_Core.Components.Core;

namespace CAP_Core.Routing;

/// <summary>
/// The connection-aware component-collision half of <see cref="WaveguideRouter"/>: ONE shared,
/// read-only predicate that judges a connection's routed geometry against component obstacles
/// with a narrow allowance around the connection's own pins. Every consumer that can encounter
/// a collapsed pin lead — the collapse acceptance, incremental route validation, the
/// frozen-route unfreeze check and the segment-shift collision flag — uses this predicate, so
/// they can never disagree about what counts as a component collision.
/// </summary>
public partial class WaveguideRouter
{
    /// <summary>
    /// Checks whether a connection's path passes through component obstacles, tolerating only
    /// the padding band immediately around the connection's OWN pins. Semantics match
    /// <see cref="IsPathBlockedByComponents"/> — component cells block, padding counts, frozen
    /// group cells block — except that a component-blocked cell is tolerated when ALL hold:
    /// it lies within the first/last bend's reach of one of the two own pins (the bend radius
    /// plus one grid cell), it is inside no component's unpadded body rectangle (foreign or
    /// own — the padding band is tolerable, a body never), and it is not a frozen group cell.
    /// Purely read-only: no grid cells are cleared or restored, so the verdict is safe against
    /// concurrent routing passes and UI reads of the grid.
    /// </summary>
    /// <param name="segments">The connection's path geometry to test.</param>
    /// <param name="startPin">The connection's own start pin.</param>
    /// <param name="endPin">The connection's own end pin.</param>
    /// <param name="bendRadius">Bend radius the route was actually built with (see
    /// <c>WaveguideConnection.RoutedBendRadiusMicrometers</c>); sizes the own-pin allowance.</param>
    public bool IsPathBlockedByComponentsForConnection(
        IEnumerable<PathSegment> segments, PhysicalPin startPin, PhysicalPin endPin,
        double bendRadius)
    {
        var grid = PathfindingGrid;
        if (grid == null) return false;

        var bodies = grid.GetComponentBodyRectangles();
        var (startX, startY) = startPin.GetAbsolutePosition();
        var (endX, endY) = endPin.GetAbsolutePosition();
        double allowanceRadius = bendRadius + grid.CellSizeMicrometers;
        double allowanceRadiusSquared = allowanceRadius * allowanceRadius;

        bool IsCellBlockedForConnection(int gridX, int gridY)
        {
            if (!grid.IsBlockedByComponent(gridX, gridY))
                return false; // free or a sibling waveguide: not a component collision
            if (!grid.IsInBounds(gridX, gridY) || grid.IsFrozenPathCell(gridX, gridY))
                return true; // out of bounds, or a group-internal waveguide: never tolerated
            var (x, y) = grid.GridToPhysical(gridX, gridY);
            if (DistanceSquared(x, y, startX, startY) > allowanceRadiusSquared &&
                DistanceSquared(x, y, endX, endY) > allowanceRadiusSquared)
                return true; // beyond the own-pin reach, components block as always
            return IsInsideAnyBody(x, y, bodies); // own-pin padding band tolerated, bodies never
        }

        return IsPathBlocked(segments, IsCellBlockedForConnection);
    }

    /// <summary>
    /// True when the point lies strictly inside any body rectangle. Strict on purpose: pins
    /// sit exactly ON body edges, so a cell centre on the edge is the legitimate attach line
    /// of a pin hug, not a penetration.
    /// </summary>
    private static bool IsInsideAnyBody(
        double x, double y, List<(double MinX, double MinY, double MaxX, double MaxY)> bodies)
    {
        foreach (var (minX, minY, maxX, maxY) in bodies)
        {
            if (x > minX && x < maxX && y > minY && y < maxY)
                return true;
        }
        return false;
    }

    private static double DistanceSquared(double x1, double y1, double x2, double y2)
    {
        double dx = x2 - x1;
        double dy = y2 - y1;
        return dx * dx + dy * dy;
    }
}
