using CAP_Core.Components.Core;

namespace CAP_Core.Routing.AStarPathfinder;

/// <summary>
/// Ownership-aware cell bookkeeping. The raster (<c>_cells</c>) only stores a state byte, so a
/// neighbouring component's padding band that covers a FOREIGN pin corridor is indistinguishable
/// from a real body cell. This part records, per component, which blocked cells are BODY cells
/// (refcounted, overlaps survive removal) and which cells belong to each pin's persistent
/// corridor, so read-only validation predicates can tolerate "route hugs its own pin under
/// foreign padding" while a foreign body inside the corridor still blocks.
/// </summary>
public partial class PathfindingGrid
{
    private readonly object _ownershipLock = new();

    /// <summary>How many registered component bodies cover a cell (padding excluded).</summary>
    private readonly Dictionary<(int x, int y), int> _bodyCellRefCounts = new();

    /// <summary>Body cells per component, for refcounted removal.</summary>
    private readonly Dictionary<Component, HashSet<(int x, int y)>> _componentBodyCells = new();

    /// <summary>Persistent pin-corridor cells per pin (registered with the pin's component).</summary>
    private readonly Dictionary<PhysicalPin, HashSet<(int x, int y)>> _pinCorridorCells = new();

    /// <summary>Pins whose corridors were registered by a component, for removal.</summary>
    private readonly Dictionary<Component, List<PhysicalPin>> _componentCorridorPins = new();

    /// <summary>
    /// Like <see cref="IsBlockedByComponent(int,int)"/>, but tolerates cells that are covered
    /// ONLY by component padding (no body, no frozen path) AND lie inside the persistent pin
    /// corridor of one of <paramref name="routePins"/> — the route's own endpoint pins. This is
    /// what lets a collapsed bend hug its pin even when a neighbouring component's padding band
    /// re-marks the corridor cells.
    /// </summary>
    /// <param name="gridX">Cell X index.</param>
    /// <param name="gridY">Cell Y index.</param>
    /// <param name="routePins">The route's own endpoint pins whose corridors are tolerated.</param>
    public bool IsBlockedByComponentForRoute(
        int gridX, int gridY, IReadOnlyCollection<PhysicalPin> routePins)
    {
        if (!IsInBounds(gridX, gridY)) return true;
        byte state = _cells[gridX, gridY];
        if (state == 3) return true;  // Frozen group paths always block.
        if (state != 1) return false; // Free or waveguide — not a component collision.

        lock (_ownershipLock)
        {
            if (_bodyCellRefCounts.ContainsKey((gridX, gridY)))
                return true; // A real component body covers the cell.
            foreach (var pin in routePins)
            {
                if (pin != null
                    && _pinCorridorCells.TryGetValue(pin, out var corridor)
                    && corridor.Contains((gridX, gridY)))
                {
                    return false; // Padding-only cell inside the route's own pin corridor.
                }
            }
        }
        return true; // Padding outside the route's own corridors blocks as before.
    }

    /// <summary>
    /// Registers a component's body cells (refcounted) and its pins' corridor cells.
    /// Replaces any previous registration for the same component.
    /// </summary>
    private void RegisterCellOwnership(Component component, HashSet<(int x, int y)> bodyCells,
        Dictionary<PhysicalPin, HashSet<(int x, int y)>> corridorCellsByPin)
    {
        lock (_ownershipLock)
        {
            UnregisterCellOwnershipLocked(component);

            _componentBodyCells[component] = bodyCells;
            foreach (var cell in bodyCells)
            {
                _bodyCellRefCounts.TryGetValue(cell, out int count);
                _bodyCellRefCounts[cell] = count + 1;
            }

            var pins = new List<PhysicalPin>(corridorCellsByPin.Count);
            foreach (var (pin, cells) in corridorCellsByPin)
            {
                _pinCorridorCells[pin] = cells;
                pins.Add(pin);
            }
            _componentCorridorPins[component] = pins;
        }
    }

    /// <summary>Removes a component's body refcounts and pin-corridor registrations.</summary>
    private void UnregisterCellOwnership(Component component)
    {
        lock (_ownershipLock)
        {
            UnregisterCellOwnershipLocked(component);
        }
    }

    private void UnregisterCellOwnershipLocked(Component component)
    {
        if (_componentBodyCells.Remove(component, out var bodyCells))
        {
            foreach (var cell in bodyCells)
            {
                if (!_bodyCellRefCounts.TryGetValue(cell, out int count)) continue;
                if (count <= 1)
                    _bodyCellRefCounts.Remove(cell);
                else
                    _bodyCellRefCounts[cell] = count - 1;
            }
        }

        if (_componentCorridorPins.Remove(component, out var pins))
        {
            foreach (var pin in pins)
                _pinCorridorCells.Remove(pin);
        }
    }

    /// <summary>Clears all ownership bookkeeping (full grid rebuild).</summary>
    private void ClearCellOwnership()
    {
        lock (_ownershipLock)
        {
            _bodyCellRefCounts.Clear();
            _componentBodyCells.Clear();
            _pinCorridorCells.Clear();
            _componentCorridorPins.Clear();
        }
    }
}
