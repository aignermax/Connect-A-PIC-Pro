using CAP_Core.Components.Core;
using System.Linq;

namespace CAP_Core.Routing.AStarPathfinder;

/// <summary>
/// Manages the pathfinding grid for A* waveguide routing.
/// Handles coordinate conversion between physical micrometers and grid cells,
/// and tracks which cells are blocked by component obstacles.
/// </summary>
public partial class PathfindingGrid
{
    /// <summary>
    /// Grid resolution in micrometers per cell.
    /// Default: 1.0 µm provides good balance of precision and performance.
    /// </summary>
    public double CellSizeMicrometers { get; }

    /// <summary>
    /// Padding around obstacles in micrometers (typically MinWaveguideSpacing / 2).
    /// </summary>
    public double ObstaclePaddingMicrometers { get; set; }

    // Grid bounds in micrometers
    public double MinX { get; private set; }
    public double MinY { get; private set; }
    public double MaxX { get; private set; }
    public double MaxY { get; private set; }

    // Grid dimensions in cells
    public int Width { get; private set; }
    public int Height { get; private set; }

    // Cell states: 0 = free, 1 = blocked by component, 2 = blocked by waveguide, 3 = blocked by frozen path (permanent)
    private byte[,] _cells;

    // Track which components own which cells (for selective invalidation)
    private readonly Dictionary<Component, HashSet<(int x, int y)>> _componentCells = new();
    private readonly object _componentCellsLock = new();

    // Track waveguide path cells (keyed by connection ID)
    private readonly Dictionary<Guid, HashSet<(int x, int y)>> _waveguideCells = new();
    private readonly object _waveguideCellsLock = new();

    // Pin reservation zones: cells near pins that get a soft cost penalty (not blocked).
    // Routes CAN pass through but A* prefers to avoid them, keeping pin areas accessible.
    private readonly HashSet<(int x, int y)> _pinZoneCells = new();
    private readonly object _pinZoneLock = new();

    /// <summary>
    /// Callback invoked when waveguide cells are added (for distance transform updates).
    /// </summary>
    public Action<HashSet<(int, int)>>? OnWaveguideCellsAdded { get; set; }

    /// <summary>
    /// Callback invoked when all waveguide obstacles are cleared (for distance transform rebuild).
    /// </summary>
    public Action? OnAllWaveguidesCleared { get; set; }

    /// <summary>
    /// Creates a grid covering the specified area.
    /// </summary>
    /// <param name="minX">Minimum X bound in micrometers</param>
    /// <param name="minY">Minimum Y bound in micrometers</param>
    /// <param name="maxX">Maximum X bound in micrometers</param>
    /// <param name="maxY">Maximum Y bound in micrometers</param>
    /// <param name="cellSize">Cell size in micrometers (default 1.0)</param>
    /// <param name="padding">Obstacle padding in micrometers (default 1.0)</param>
    public PathfindingGrid(double minX, double minY, double maxX, double maxY,
                           double cellSize = 1.0, double padding = 1.0)
    {
        CellSizeMicrometers = cellSize;
        ObstaclePaddingMicrometers = padding;
        MinX = minX;
        MinY = minY;
        MaxX = maxX;
        MaxY = maxY;

        Width = (int)Math.Ceiling((maxX - minX) / cellSize);
        Height = (int)Math.Ceiling((maxY - minY) / cellSize);

        // Ensure minimum grid size
        Width = Math.Max(Width, 1);
        Height = Math.Max(Height, 1);

        _cells = new byte[Width, Height];
    }

    /// <summary>
    /// Converts physical micrometers to grid cell coordinates.
    /// </summary>
    public (int gridX, int gridY) PhysicalToGrid(double physicalX, double physicalY)
    {
        int gridX = (int)Math.Floor((physicalX - MinX) / CellSizeMicrometers);
        int gridY = (int)Math.Floor((physicalY - MinY) / CellSizeMicrometers);
        return (Math.Clamp(gridX, 0, Width - 1), Math.Clamp(gridY, 0, Height - 1));
    }

    /// <summary>
    /// Converts grid cell to physical micrometers (center of cell).
    /// </summary>
    public (double x, double y) GridToPhysical(int gridX, int gridY)
    {
        double x = MinX + (gridX + 0.5) * CellSizeMicrometers;
        double y = MinY + (gridY + 0.5) * CellSizeMicrometers;
        return (x, y);
    }

    /// <summary>
    /// Marks cells blocked by a component's bounding box (with padding).
    /// For ComponentGroup instances, recursively adds obstacles for each child component
    /// instead of blocking the entire group bounding box.
    /// Leaves corridors open at pin positions so waveguides can connect.
    /// </summary>
    public void AddComponentObstacle(Component component)
    {
        // Handle ComponentGroup specially - add obstacles for child components instead of group bounds
        if (component is ComponentGroup group)
        {
            AddComponentGroupObstacle(group);
            return;
        }

        // Regular component obstacle handling
        AddSingleComponentObstacle(component);
    }

    /// <summary>
    /// Adds obstacles for a ComponentGroup by recursively adding child component obstacles
    /// and marking frozen waveguide paths as obstacles.
    /// This allows waveguides to route through empty space between grouped components
    /// but prevents routing through the group's internal connections.
    /// </summary>
    private void AddComponentGroupObstacle(ComponentGroup group)
    {
        var groupCells = new HashSet<(int, int)>();

        // Add obstacles for child components
        foreach (var child in group.ChildComponents)
        {
            if (child is ComponentGroup childGroup)
            {
                // Recursively handle nested groups
                AddComponentGroupObstacle(childGroup);
            }
            else
            {
                // Add obstacle for regular child component
                AddSingleComponentObstacle(child);
            }
        }

        // Add obstacles for frozen waveguide paths (internal connections)
        // These are stored in the group as FrozenWaveguidePath instances
        foreach (var frozenPath in group.InternalPaths)
        {
            if (frozenPath?.Path?.Segments == null) continue;

            // Convert RoutedPath segments to PathSegments
            var pathSegments = new List<PathSegment>();
            foreach (var segment in frozenPath.Path.Segments)
            {
                pathSegments.Add(segment);
            }

            // Mark these cells as frozen path obstacles (state=3) which are NEVER cleared by ClearPinCorridor
            // This prevents external routing from going through internal group connections.
            // Use at least CellSizeMicrometers as width to guarantee cells are always marked:
            // a circle-based mark only covers a cell if the cell center is within the radius,
            // so the radius must be >= CellSizeMicrometers/2 to cover the containing cell.
            double frozenPathMarkWidth = Math.Max(2.0, CellSizeMicrometers);
            var pathCells = GetWaveguidePathCells(pathSegments, frozenPathMarkWidth);
            foreach (var cell in pathCells)
            {
                if (IsInBounds(cell.Item1, cell.Item2))
                {
                    // Mark as frozen path obstacle (state=3) - permanent and never cleared
                    // This takes precedence over component obstacles (state=1)
                    if (_cells[cell.Item1, cell.Item2] != 3)
                    {
                        _cells[cell.Item1, cell.Item2] = 3; // Mark as frozen path obstacle (permanent)
                    }
                    groupCells.Add(cell);
                }
            }
        }

        // Track all cells occupied by this group (for removal)
        lock (_componentCellsLock)
        {
            _componentCells[group] = groupCells;
        }
    }

    /// <summary>
    /// Gets grid cells occupied by a waveguide path (with waveguide width).
    /// </summary>
    private HashSet<(int, int)> GetWaveguidePathCells(List<PathSegment> segments, double waveguideWidth)
    {
        var cells = new HashSet<(int, int)>();
        double halfWidth = waveguideWidth / 2;

        foreach (var segment in segments)
        {
            if (segment is StraightSegment straight)
            {
                MarkLineAsCells(straight.StartPoint.X, straight.StartPoint.Y,
                    straight.EndPoint.X, straight.EndPoint.Y, halfWidth, cells);
            }
            else if (segment is BendSegment bend)
            {
                // Mark cells along arc - sample points along the arc
                double startRad = bend.StartAngleDegrees * Math.PI / 180;
                double sweepRad = bend.SweepAngleDegrees * Math.PI / 180;
                int numSamples = Math.Max(10, (int)(Math.Abs(bend.SweepAngleDegrees) / 5));

                for (int i = 0; i <= numSamples; i++)
                {
                    double t = (double)i / numSamples;
                    double angle = startRad + sweepRad * t;

                    // Point on arc
                    double sign = Math.Sign(bend.SweepAngleDegrees);
                    if (sign == 0) sign = 1;

                    double px = bend.Center.X + bend.RadiusMicrometers * Math.Cos(angle - Math.PI / 2 * sign);
                    double py = bend.Center.Y + bend.RadiusMicrometers * Math.Sin(angle - Math.PI / 2 * sign);

                    MarkCircleAsCells(px, py, halfWidth, cells);
                }
            }
        }

        return cells;
    }

    /// <summary>
    /// Adds obstacle cells for a single (non-group) component.
    /// </summary>
    private void AddSingleComponentObstacle(Component component)
    {
        double padding = ObstaclePaddingMicrometers;
        double x1 = component.PhysicalX - padding;
        double y1 = component.PhysicalY - padding;
        double x2 = component.PhysicalX + component.WidthMicrometers + padding;
        double y2 = component.PhysicalY + component.HeightMicrometers + padding;

        var (gx1, gy1) = PhysicalToGrid(x1, y1);
        var (gx2, gy2) = PhysicalToGrid(x2, y2);

        // Collect pin corridor cells that should remain open
        // Only clear a small area OUTSIDE the component where the waveguide approaches
        var pinCorridorCells = new HashSet<(int, int)>();
        double corridorLength = 10.0; // Length of corridor OUTWARD from pin (not into component)
        double corridorWidth = 4.0;   // Narrow corridor width (just enough for waveguide)

        foreach (var pin in component.PhysicalPins)
        {
            var (pinX, pinY) = pin.GetAbsolutePosition();
            double pinAngle = pin.GetAbsoluteAngle();

            // Calculate corridor going OUTWARD from the component (same direction as pin points)
            double angleRad = pinAngle * Math.PI / 180.0;
            double dx = Math.Cos(angleRad);
            double dy = Math.Sin(angleRad);
            double perpX = -dy;
            double perpY = dx;

            // Mark corridor cells - only going outward from the pin
            for (double dist = 0; dist <= corridorLength; dist += CellSizeMicrometers)
            {
                double centerX = pinX + dx * dist;
                double centerY = pinY + dy * dist;

                for (double offset = -corridorWidth / 2; offset <= corridorWidth / 2; offset += CellSizeMicrometers)
                {
                    double cellX = centerX + perpX * offset;
                    double cellY = centerY + perpY * offset;
                    var (gx, gy) = PhysicalToGrid(cellX, cellY);
                    pinCorridorCells.Add((gx, gy));
                }
            }
        }

        var cells = new HashSet<(int, int)>();
        for (int gx = gx1; gx <= gx2; gx++)
        {
            for (int gy = gy1; gy <= gy2; gy++)
            {
                if (IsInBounds(gx, gy) && !pinCorridorCells.Contains((gx, gy)))
                {
                    _cells[gx, gy] = 1; // Blocked by obstacle
                    cells.Add((gx, gy));
                }
            }
        }
        lock (_componentCellsLock)
        {
            _componentCells[component] = cells;
        }

        // Mark pin reservation zones — soft penalty area around each pin.
        // Routes can pass through but A* prefers to avoid them.
        double pinZoneRadius = 15.0; // µm around each pin
        foreach (var pin in component.PhysicalPins)
        {
            var (pinX, pinY) = pin.GetAbsolutePosition();
            MarkPinReservationZone(pinX, pinY, pinZoneRadius);
        }
    }

    /// <summary>
    /// Removes obstacle cells for a component (when deleted or moved).
    /// For ComponentGroup instances, recursively removes child component obstacles.
    /// </summary>
    public void RemoveComponentObstacle(Component component)
    {
        // Handle ComponentGroup specially - remove obstacles for all children
        if (component is ComponentGroup group)
        {
            RemoveComponentGroupObstacle(group);
            return;
        }

        // Regular component obstacle removal
        HashSet<(int x, int y)>? cells;
        lock (_componentCellsLock)
        {
            _componentCells.TryGetValue(component, out cells);
            if (cells != null)
                _componentCells.Remove(component);
        }
        if (cells != null)
        {
            foreach (var (gx, gy) in cells)
            {
                if (IsInBounds(gx, gy))
                {
                    _cells[gx, gy] = 0;
                }
            }
        }
    }

    /// <summary>
    /// Removes obstacles for a ComponentGroup by recursively removing child component obstacles
    /// and clearing frozen waveguide path obstacles.
    /// </summary>
    private void RemoveComponentGroupObstacle(ComponentGroup group)
    {
        // Recursively remove obstacles for each child
        foreach (var child in group.ChildComponents)
        {
            if (child is ComponentGroup childGroup)
            {
                RemoveComponentGroupObstacle(childGroup);
            }
            else
            {
                // Remove obstacle for regular child component
                HashSet<(int x, int y)>? cells;
                lock (_componentCellsLock)
                {
                    _componentCells.TryGetValue(child, out cells);
                    if (cells != null)
                        _componentCells.Remove(child);
                }
                if (cells != null)
                {
                    foreach (var (gx, gy) in cells)
                    {
                        if (IsInBounds(gx, gy))
                        {
                            _cells[gx, gy] = 0;
                        }
                    }
                }
            }
        }

        // Remove the group's own cells (frozen paths marked with state=3)
        HashSet<(int x, int y)>? groupCells;
        lock (_componentCellsLock)
        {
            _componentCells.TryGetValue(group, out groupCells);
            _componentCells.Remove(group);
        }
        if (groupCells != null)
        {
            foreach (var (gx, gy) in groupCells)
            {
                if (IsInBounds(gx, gy) && _cells[gx, gy] == 3)
                {
                    _cells[gx, gy] = 0;
                }
            }
        }
    }

    /// <summary>
    /// Updates component obstacle (for moves). Removes old cells and adds new ones.
    /// </summary>
    public void UpdateComponentObstacle(Component component)
    {
        RemoveComponentObstacle(component);
        AddComponentObstacle(component);
    }

    /// <summary>
    /// Temporarily clears cells in a rectangular area.
    /// Used to ensure pins can be reached even if inside component bounds.
    /// Returns the cells that were cleared so they can be restored (with their original state).
    /// </summary>
    public Dictionary<(int x, int y), byte> ClearArea(double physX, double physY, double width, double height)
    {
        var clearedCells = new Dictionary<(int, int), byte>();

        var (gx1, gy1) = PhysicalToGrid(physX, physY);
        var (gx2, gy2) = PhysicalToGrid(physX + width, physY + height);

        for (int gx = gx1; gx <= gx2; gx++)
        {
            for (int gy = gy1; gy <= gy2; gy++)
            {
                if (IsInBounds(gx, gy) && _cells[gx, gy] != 0)
                {
                    clearedCells[(gx, gy)] = _cells[gx, gy]; // Store original state
                    _cells[gx, gy] = 0;
                }
            }
        }

        return clearedCells;
    }

    /// <summary>
    /// Restores previously cleared cells to their original state.
    /// </summary>
    public void RestoreCells(Dictionary<(int x, int y), byte> cells)
    {
        foreach (var ((gx, gy), state) in cells)
        {
            if (IsInBounds(gx, gy))
            {
                _cells[gx, gy] = state; // Restore original state (1 or 2)
            }
        }
    }

    /// <summary>
    /// Clears a corridor from a pin position in its direction.
    /// This ensures the pathfinder can start/end at the pin even if it's
    /// close to component edges.
    /// Only clears component obstacles (state=1), NOT waveguide obstacles (state=2).
    /// </summary>
    public Dictionary<(int x, int y), byte> ClearPinCorridor(double pinX, double pinY, double angleDegrees, double corridorLength, double corridorWidth)
    {
        var clearedCells = new Dictionary<(int, int), byte>();

        double angleRad = angleDegrees * Math.PI / 180.0;
        double dx = Math.Cos(angleRad);
        double dy = Math.Sin(angleRad);

        // Perpendicular direction for corridor width
        double perpX = -dy;
        double perpY = dx;

        // Clear cells along the corridor - only component obstacles, not waveguides
        for (double dist = 0; dist <= corridorLength; dist += CellSizeMicrometers)
        {
            double centerX = pinX + dx * dist;
            double centerY = pinY + dy * dist;

            // Clear across the corridor width
            for (double offset = -corridorWidth / 2; offset <= corridorWidth / 2; offset += CellSizeMicrometers)
            {
                double cellX = centerX + perpX * offset;
                double cellY = centerY + perpY * offset;

                var (gx, gy) = PhysicalToGrid(cellX, cellY);
                // Only clear component obstacles (1), NOT waveguide obstacles (2)
                if (IsInBounds(gx, gy) && _cells[gx, gy] == 1)
                {
                    clearedCells[(gx, gy)] = _cells[gx, gy];
                    _cells[gx, gy] = 0;
                }
            }
        }

        return clearedCells;
    }

    /// <summary>
    /// Checks if a cell is blocked.
    /// </summary>
    public bool IsBlocked(int gridX, int gridY)
    {
        return !IsInBounds(gridX, gridY) || _cells[gridX, gridY] != 0;
    }

    /// <summary>
    /// Gets the state of a cell.
    /// Returns: 0 = free, 1 = blocked by component, 2 = blocked by waveguide
    /// </summary>
    public byte GetCellState(int gridX, int gridY)
    {
        if (!IsInBounds(gridX, gridY))
            return 1; // Out of bounds = blocked
        return _cells[gridX, gridY];
    }

    /// <summary>
    /// Sets the state of a cell directly. Used for testing and manual grid manipulation.
    /// </summary>
    public void SetCellState(int gridX, int gridY, byte state)
    {
        if (IsInBounds(gridX, gridY))
            _cells[gridX, gridY] = state;
    }

    /// <summary>
    /// Checks if coordinates are within grid bounds.
    /// </summary>
    public bool IsInBounds(int gridX, int gridY)
    {
        return gridX >= 0 && gridX < Width && gridY >= 0 && gridY < Height;
    }

    /// <summary>
    /// Rebuilds the entire grid from a list of components.
    /// Use for initial setup or full invalidation.
    /// </summary>
    public void RebuildFromComponents(IEnumerable<Component> components)
    {
        Array.Clear(_cells);
        lock (_componentCellsLock)
        {
            _componentCells.Clear();
        }
        lock (_waveguideCellsLock)
        {
            _waveguideCells.Clear();
            _waveguideEndpoints.Clear();
        }
        lock (_pinZoneLock)
        {
            _pinZoneCells.Clear();
        }

        foreach (var component in components)
        {
            AddComponentObstacle(component);
        }
    }

    /// <summary>
    /// Gets the number of blocked cells (for debugging/statistics).
    /// </summary>
    public int GetBlockedCellCount()
    {
        int count = 0;
        for (int x = 0; x < Width; x++)
        {
            for (int y = 0; y < Height; y++)
            {
                if (_cells[x, y] != 0) count++;
            }
        }
        return count;
    }

    /// <summary>
    /// Marks cells along a waveguide path as blocked.
    /// Used for sequential routing to avoid collisions with already-routed waveguides.
    /// Blocks the ENTIRE path including near pins - ClearPinCorridor handles new connections.
    /// </summary>
    /// <param name="connectionId">Unique ID of the waveguide connection</param>
    /// <param name="segments">Path segments to mark as obstacles</param>
    /// <param name="waveguideWidth">Width of the waveguide in micrometers</param>
    public void AddWaveguideObstacle(Guid connectionId, IEnumerable<PathSegment> segments, double waveguideWidth)
    {
        // First remove any existing obstacle for this connection
        RemoveWaveguideObstacle(connectionId);

        var segmentList = segments.ToList();
        if (segmentList.Count == 0) return;

        var cells = new HashSet<(int, int)>();
        // For waveguides, don't add the component padding - just use the waveguide width directly
        // The waveguideWidth parameter already includes the desired clearance
        double halfWidth = waveguideWidth / 2;

        // Block the ENTIRE waveguide path - no exclusion zones
        // When routing new waveguides, ClearPinCorridor() temporarily clears the area
        // around pins to allow new connections to start
        foreach (var segment in segmentList)
        {
            if (segment is StraightSegment straight)
            {
                MarkLineAsCells(straight.StartPoint.X, straight.StartPoint.Y,
                    straight.EndPoint.X, straight.EndPoint.Y, halfWidth, cells);
            }
            else if (segment is BendSegment bend)
            {
                // Mark cells along arc - sample points along the arc
                double startRad = bend.StartAngleDegrees * Math.PI / 180;
                double sweepRad = bend.SweepAngleDegrees * Math.PI / 180;
                int numSamples = Math.Max(10, (int)(Math.Abs(bend.SweepAngleDegrees) / 5));

                for (int i = 0; i <= numSamples; i++)
                {
                    double t = (double)i / numSamples;
                    double angle = startRad + sweepRad * t;

                    // Point on arc (perpendicular to tangent direction)
                    double sign = Math.Sign(bend.SweepAngleDegrees);
                    if (sign == 0) sign = 1;

                    double px = bend.Center.X + bend.RadiusMicrometers * Math.Cos(angle - Math.PI / 2 * sign);
                    double py = bend.Center.Y + bend.RadiusMicrometers * Math.Sin(angle - Math.PI / 2 * sign);

                    MarkCircleAsCells(px, py, halfWidth, cells);
                }
            }
        }

        // Apply cells to grid
        foreach (var (gx, gy) in cells)
        {
            if (IsInBounds(gx, gy) && _cells[gx, gy] == 0)
            {
                _cells[gx, gy] = 2; // Blocked by waveguide
            }
        }

        lock (_waveguideCellsLock)
        {
            _waveguideCells[connectionId] = cells;
            _waveguideEndpoints[connectionId] = (
                (segmentList[0].StartPoint.X, segmentList[0].StartPoint.Y),
                (segmentList[^1].EndPoint.X, segmentList[^1].EndPoint.Y));
        }
        OnWaveguideCellsAdded?.Invoke(cells);
    }

    /// <summary>
    /// Removes a waveguide obstacle from the grid.
    /// </summary>
    public void RemoveWaveguideObstacle(Guid connectionId)
    {
        HashSet<(int, int)>? cells;
        lock (_waveguideCellsLock)
        {
            if (!_waveguideCells.TryGetValue(connectionId, out cells))
                return;
            _waveguideCells.Remove(connectionId);
            _waveguideEndpoints.Remove(connectionId);
        }

        foreach (var (gx, gy) in cells)
        {
            if (IsInBounds(gx, gy) && _cells[gx, gy] == 2)
            {
                _cells[gx, gy] = 0;
            }
        }
    }

    /// <summary>
    /// Clears all waveguide obstacles from the grid.
    /// </summary>
    public void ClearAllWaveguideObstacles()
    {
        List<Guid> connectionIds;
        lock (_waveguideCellsLock)
        {
            connectionIds = _waveguideCells.Keys.ToList();
        }

        foreach (var connectionId in connectionIds)
        {
            RemoveWaveguideObstacle(connectionId);
        }
        OnAllWaveguidesCleared?.Invoke();
    }

    /// <summary>
    /// Checks whether a physical bounding box (micrometers) is free for placing a
    /// crossing component: no component or frozen-path cells inside, and any
    /// waveguide cells must belong exclusively to the allowed connections (the two
    /// nets being crossed). Used by adaptive crossing insertion.
    /// </summary>
    /// <param name="minX">Left edge of the box in micrometers.</param>
    /// <param name="minY">Top edge of the box in micrometers.</param>
    /// <param name="maxX">Right edge of the box in micrometers.</param>
    /// <param name="maxY">Bottom edge of the box in micrometers.</param>
    /// <param name="allowedConnectionIds">Connection IDs whose waveguide cells may occupy the box.</param>
    public bool IsAreaClearForCrossing(
        double minX, double minY, double maxX, double maxY, ISet<Guid> allowedConnectionIds)
    {
        var (gx1, gy1) = PhysicalToGrid(minX, minY);
        var (gx2, gy2) = PhysicalToGrid(maxX, maxY);

        HashSet<(int, int)> allowedCells = new();
        lock (_waveguideCellsLock)
        {
            foreach (var id in allowedConnectionIds)
            {
                if (_waveguideCells.TryGetValue(id, out var cells))
                    allowedCells.UnionWith(cells);
            }
        }

        for (int gx = gx1; gx <= gx2; gx++)
        {
            for (int gy = gy1; gy <= gy2; gy++)
            {
                byte state = GetCellState(gx, gy);
                if (state == 0) continue;
                if (state != 2) return false; // component or frozen path
                if (!allowedCells.Contains((gx, gy))) return false; // third-party waveguide
            }
        }
        return true;
    }

    /// <summary>
    /// Checks if a cell is in a pin reservation zone (soft penalty, not blocked).
    /// </summary>
    public bool IsPinReservationZone(int gridX, int gridY)
    {
        lock (_pinZoneLock)
        {
            return _pinZoneCells.Contains((gridX, gridY));
        }
    }

    /// <summary>
    /// Marks a circular zone around a pin position as a reservation zone.
    /// Only marks cells that are currently free (state=0) — doesn't mark obstacles.
    /// </summary>
    private void MarkPinReservationZone(double pinX, double pinY, double radiusMicrometers)
    {
        var (gcx, gcy) = PhysicalToGrid(pinX, pinY);
        int gridRadius = (int)Math.Ceiling(radiusMicrometers / CellSizeMicrometers);

        lock (_pinZoneLock)
        {
            for (int gx = gcx - gridRadius; gx <= gcx + gridRadius; gx++)
            {
                for (int gy = gcy - gridRadius; gy <= gcy + gridRadius; gy++)
                {
                    if (!IsInBounds(gx, gy)) continue;

                    var (px, py) = GridToPhysical(gx, gy);
                    double dist = Math.Sqrt((px - pinX) * (px - pinX) + (py - pinY) * (py - pinY));
                    if (dist <= radiusMicrometers)
                    {
                        _pinZoneCells.Add((gx, gy));
                    }
                }
            }
        }
    }

    /// <summary>
    /// Marks cells along a line with given half-width.
    /// </summary>
    private void MarkLineAsCells(double x1, double y1, double x2, double y2, double halfWidth, HashSet<(int, int)> cells)
    {
        double dx = x2 - x1;
        double dy = y2 - y1;
        double length = Math.Sqrt(dx * dx + dy * dy);

        if (length < 0.001) return;

        // Normalize direction
        dx /= length;
        dy /= length;

        // Sample points along the line
        double step = CellSizeMicrometers * 0.5;
        for (double t = 0; t <= length; t += step)
        {
            double px = x1 + dx * t;
            double py = y1 + dy * t;
            MarkCircleAsCells(px, py, halfWidth, cells);
        }
    }

    /// <summary>
    /// Marks cells in a circle around a point.
    /// </summary>
    private void MarkCircleAsCells(double cx, double cy, double radius, HashSet<(int, int)> cells)
    {
        var (gcx, gcy) = PhysicalToGrid(cx, cy);
        int gridRadius = (int)Math.Ceiling(radius / CellSizeMicrometers);

        for (int gx = gcx - gridRadius; gx <= gcx + gridRadius; gx++)
        {
            for (int gy = gcy - gridRadius; gy <= gcy + gridRadius; gy++)
            {
                if (IsInBounds(gx, gy))
                {
                    // Check if cell center is within radius
                    var (px, py) = GridToPhysical(gx, gy);
                    double dist = Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
                    if (dist <= radius)
                    {
                        cells.Add((gx, gy));
                    }
                }
            }
        }
    }
}
