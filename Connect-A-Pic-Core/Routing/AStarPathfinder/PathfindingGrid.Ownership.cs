using CAP_Core.Components.Core;

namespace CAP_Core.Routing.AStarPathfinder;

/// <summary>
/// Ownership-aware cell bookkeeping for <see cref="PathfindingGrid"/>. Rasterizing a
/// component records for every blocked cell WHO blocked it and HOW (body rectangle vs.
/// surrounding padding band), and keeps the carved pin-corridor cells per pin. Read-only
/// predicates can then distinguish "the route hugs its own pin inside that pin's corridor,
/// and only a foreign padding band reaches across" (tolerated — the corridor belongs to the
/// route) from "a foreign component body occupies the corridor" (a real collision).
/// The cell states themselves are untouched — routing cost and obstacle semantics do not
/// change; this data only feeds the collision verdicts of already-routed paths.
/// </summary>
public partial class PathfindingGrid
{
    /// <summary>How a component occupies a blocked cell.</summary>
    private enum ComponentCellKind
    {
        /// <summary>Cell lies inside the component's physical body rectangle.</summary>
        Body,

        /// <summary>Cell lies in the padding band around the body (keep-out for spacing).</summary>
        Padding,
    }

    /// <summary>One component's claim on a blocked cell.</summary>
    private readonly record struct CellOwnership(Component Owner, ComponentCellKind Kind);

    // Who blocks a cell (a cell can be claimed by several overlapping components).
    private readonly Dictionary<(int x, int y), List<CellOwnership>> _cellOwners = new();

    // Rasterized pin-corridor cells per pin — the same geometry the obstacle rasterization
    // carves out of its own component's blocked cells.
    private readonly Dictionary<PhysicalPin, HashSet<(int x, int y)>> _pinCorridorCells = new();

    // Which pins a component registered corridors for, so removal does not depend on the
    // component's current (possibly already mutated) pin list.
    private readonly Dictionary<Component, List<PhysicalPin>> _componentCorridorPins = new();
    private readonly object _ownershipLock = new();

    /// <summary>
    /// Records the ownership of a component's freshly blocked cells and its pin corridors.
    /// Called by the obstacle rasterization right after the cells were marked.
    /// </summary>
    private void RegisterComponentOwnership(
        Component component,
        HashSet<(int x, int y)> bodyCells,
        HashSet<(int x, int y)> paddingCells,
        Dictionary<PhysicalPin, HashSet<(int x, int y)>> pinCorridors)
    {
        lock (_ownershipLock)
        {
            foreach (var cell in bodyCells)
                AddOwnership(cell, component, ComponentCellKind.Body);
            foreach (var cell in paddingCells)
                AddOwnership(cell, component, ComponentCellKind.Padding);

            foreach (var (pin, cells) in pinCorridors)
                _pinCorridorCells[pin] = cells;
            _componentCorridorPins[component] = pinCorridors.Keys.ToList();
        }
    }

    /// <summary>
    /// Drops a component's ownership claims and pin corridors. The freed cells become free
    /// in the cell grid at the same time, so their whole owner list is discarded — a claim
    /// by an overlapping second component is re-registered when that component is next
    /// (re)added, mirroring the cell-state semantics of obstacle removal.
    /// </summary>
    private void UnregisterComponentOwnership(Component component, IEnumerable<(int x, int y)>? freedCells)
    {
        lock (_ownershipLock)
        {
            if (freedCells != null)
            {
                foreach (var cell in freedCells)
                    _cellOwners.Remove(cell);
            }
            if (_componentCorridorPins.Remove(component, out var pins))
            {
                foreach (var pin in pins)
                    _pinCorridorCells.Remove(pin);
            }
        }
    }

    /// <summary>Clears all ownership and pin-corridor bookkeeping (grid rebuild).</summary>
    private void ClearOwnership()
    {
        lock (_ownershipLock)
        {
            _cellOwners.Clear();
            _pinCorridorCells.Clear();
            _componentCorridorPins.Clear();
        }
    }

    private void AddOwnership((int x, int y) cell, Component owner, ComponentCellKind kind)
    {
        if (!_cellOwners.TryGetValue(cell, out var owners))
        {
            owners = new List<CellOwnership>();
            _cellOwners[cell] = owners;
        }
        owners.Add(new CellOwnership(owner, kind));
    }

    /// <summary>
    /// Union of the rasterized pin-corridor cells of the given pins, as carved when their
    /// components were registered as obstacles. Pins without a registered corridor (their
    /// component is no routing obstacle) contribute nothing.
    /// </summary>
    public HashSet<(int x, int y)> GetPinCorridorCells(IEnumerable<PhysicalPin> pins)
    {
        var result = new HashSet<(int x, int y)>();
        lock (_ownershipLock)
        {
            foreach (var pin in pins)
            {
                if (_pinCorridorCells.TryGetValue(pin, out var cells))
                    result.UnionWith(cells);
            }
        }
        return result;
    }

    /// <summary>
    /// Like <see cref="IsBlockedByComponent(int, int)"/>, but tolerates a route hugging its
    /// own pins: a blocked cell inside one of the <paramref name="toleratedCorridorCells"/>
    /// (the corridors of the route's own endpoint pins) counts as blocked only when a
    /// component BODY claims it — foreign padding reaching across the corridor does not.
    /// Frozen group path markings (state 3) stay blocking regardless.
    /// </summary>
    public bool IsBlockedByComponent(int gridX, int gridY, IReadOnlySet<(int x, int y)> toleratedCorridorCells)
    {
        if (!IsInBounds(gridX, gridY)) return true;
        byte state = _cells[gridX, gridY];
        if (state == 3) return true;
        if (state != 1) return false;
        return BlocksDespiteCorridorTolerance(gridX, gridY, toleratedCorridorCells);
    }

    /// <summary>
    /// Like <see cref="IsBlockedByComponentOnly(int, int)"/> (component geometry only,
    /// excluding frozen group path markings) with the same own-pin-corridor tolerance as
    /// the <see cref="IsBlockedByComponent(int, int)"/> overload above.
    /// </summary>
    public bool IsBlockedByComponentOnly(int gridX, int gridY, IReadOnlySet<(int x, int y)> toleratedCorridorCells)
    {
        if (!IsInBounds(gridX, gridY)) return true;
        if (_cells[gridX, gridY] != 1) return false;
        return BlocksDespiteCorridorTolerance(gridX, gridY, toleratedCorridorCells);
    }

    /// <summary>
    /// True when a component-blocked cell stays blocking despite the corridor tolerance:
    /// either it lies outside the tolerated corridors, or a body (not just padding) claims
    /// it. A blocked cell without any ownership record stays blocking — conservative.
    /// </summary>
    private bool BlocksDespiteCorridorTolerance(
        int gridX, int gridY, IReadOnlySet<(int x, int y)> toleratedCorridorCells)
    {
        if (!toleratedCorridorCells.Contains((gridX, gridY)))
            return true;
        lock (_ownershipLock)
        {
            if (!_cellOwners.TryGetValue((gridX, gridY), out var owners) || owners.Count == 0)
                return true;
            return owners.Any(owner => owner.Kind == ComponentCellKind.Body);
        }
    }
}
