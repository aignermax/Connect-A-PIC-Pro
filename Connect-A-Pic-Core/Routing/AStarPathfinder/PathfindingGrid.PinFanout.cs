namespace CAP_Core.Routing.AStarPathfinder;

/// <summary>
/// Pin fan-out handling for <see cref="PathfindingGrid"/>. Flat PDK components (e.g.
/// Cornerstone SiN "Coupler Straight", 2.636 µm tall with a 1.436 µm pin pitch) place
/// neighboring pins closer together than one grid cell and one waveguide obstacle width,
/// so a sibling's registered route buries the neighboring pin in blocked cells and A*
/// cannot even start. This part clears exactly the single-cell line of cells along the
/// pin's outward axis — and only cells owned by routes that terminate right next to the
/// pin (fan-out siblings) — so the pin becomes routable while every other sibling cell
/// stays blocked and foreign waveguides are never opened up.
/// </summary>
public partial class PathfindingGrid
{
    /// <summary>Route endpoints per registered waveguide obstacle (fan-out sibling detection).</summary>
    private readonly Dictionary<Guid, ((double X, double Y) Start, (double X, double Y) End)> _waveguideEndpoints = new();

    /// <summary>
    /// Maximum distance (µm) between a pin and a sibling route endpoint to count as the
    /// same fan-out cluster. Covers real PDK pin pitches (1.436–5.2 µm) without reaching
    /// unrelated routes.
    /// </summary>
    private const double FanoutEndpointRadiusMicrometers = 6.0;

    /// <summary>
    /// Temporarily clears waveguide cells (state=2) on the single-cell-wide line of cells
    /// along a pin's outward axis, restricted to cells owned by fan-out sibling routes.
    /// Returns the cleared cells for <see cref="RestoreCells"/>.
    /// </summary>
    /// <param name="pinX">Pin X position in micrometers.</param>
    /// <param name="pinY">Pin Y position in micrometers.</param>
    /// <param name="outwardAngleDegrees">Direction pointing away from the component.</param>
    /// <param name="corridorLength">Length of the cleared line in micrometers.</param>
    public Dictionary<(int x, int y), byte> ClearPinFanoutWaveguideCells(
        double pinX, double pinY, double outwardAngleDegrees, double corridorLength)
    {
        var cleared = new Dictionary<(int, int), byte>();
        var siblingCells = CollectFanoutSiblingCells(pinX, pinY);
        if (siblingCells.Count == 0)
            return cleared;

        double angleRad = outwardAngleDegrees * Math.PI / 180.0;
        double dx = Math.Cos(angleRad);
        double dy = Math.Sin(angleRad);

        for (double dist = 0; dist <= corridorLength; dist += CellSizeMicrometers)
        {
            var (gx, gy) = PhysicalToGrid(pinX + dx * dist, pinY + dy * dist);
            if (!IsInBounds(gx, gy) || _cells[gx, gy] != 2) continue;
            if (!siblingCells.Contains((gx, gy)) || cleared.ContainsKey((gx, gy))) continue;

            cleared[(gx, gy)] = _cells[gx, gy];
            _cells[gx, gy] = 0;
        }

        return cleared;
    }

    /// <summary>
    /// Collects the grid cells of all registered routes that have an endpoint within
    /// <see cref="FanoutEndpointRadiusMicrometers"/> of the given pin.
    /// </summary>
    private HashSet<(int, int)> CollectFanoutSiblingCells(double pinX, double pinY)
    {
        var siblingCells = new HashSet<(int, int)>();
        lock (_waveguideCellsLock)
        {
            foreach (var (connectionId, (start, end)) in _waveguideEndpoints)
            {
                if (DistanceTo(start, pinX, pinY) > FanoutEndpointRadiusMicrometers &&
                    DistanceTo(end, pinX, pinY) > FanoutEndpointRadiusMicrometers)
                    continue;
                if (_waveguideCells.TryGetValue(connectionId, out var cells))
                    siblingCells.UnionWith(cells);
            }
        }
        return siblingCells;
    }

    private static double DistanceTo((double X, double Y) point, double x, double y)
    {
        double dx = point.X - x;
        double dy = point.Y - y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
