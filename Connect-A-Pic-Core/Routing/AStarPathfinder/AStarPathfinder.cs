namespace CAP_Core.Routing.AStarPathfinder;

/// <summary>
/// A* pathfinder for waveguide routing with direction-aware node expansion.
/// Finds optimal paths while respecting turn costs and minimum straight run constraints.
/// </summary>
public class AStarPathfinder
{
    private readonly PathfindingGrid _grid;
    private readonly RoutingCostCalculator _costCalculator;

    /// <summary>
    /// Maximum nodes to expand before giving up (prevents infinite search on large grids).
    /// Lower values = faster but may miss longer paths.
    /// Increased to 200000 to handle complex layouts with many obstacles.
    /// </summary>
    public int MaxNodesExpanded { get; set; } = 200000;

    /// <summary>
    /// Distance tolerance for reaching the goal (in grid cells).
    /// With 5µm cells, 3 cells = 15µm tolerance.
    /// </summary>
    public int GoalTolerance { get; set; } = 3;

    public AStarPathfinder(PathfindingGrid grid, RoutingCostCalculator costCalculator)
    {
        _grid = grid;
        _costCalculator = costCalculator;
    }

    /// <summary>
    /// How often (in node expansions) to check the cancellation token.
    /// Lower values = more responsive cancellation, slight overhead per check.
    /// </summary>
    private const int CancellationCheckInterval = 500;

    /// <summary>
    /// Maximum allowed direction change per step in degrees.
    /// Turns sharper than 90° cannot be built as a single fabricable bend.
    /// </summary>
    private const double MaxTurnAngleDegrees = 90.0;

    /// <summary>
    /// Finds a path from start to end, respecting pin directions.
    /// </summary>
    /// <param name="startX">Start position X in grid cells</param>
    /// <param name="startY">Start position Y in grid cells</param>
    /// <param name="startDirection">Required initial direction (from pin angle)</param>
    /// <param name="endX">End position X in grid cells</param>
    /// <param name="endY">End position Y in grid cells</param>
    /// <param name="endDirection">Required final direction (direction to enter end pin)</param>
    /// <param name="cancellationToken">Token to cancel the search (checked every 500 nodes).</param>
    /// <returns>List of nodes forming the path, or null if no path found or cancelled</returns>
    public List<AStarNode>? FindPath(int startX, int startY, GridDirection startDirection,
                                      int endX, int endY, GridDirection endDirection,
                                      CancellationToken cancellationToken = default)
    {
        var openSet = new PriorityQueue<AStarNode, double>();
        var visited = new Dictionary<(int, int, GridDirection, int), AStarNode>();
        var distanceFromStart = new Dictionary<(int, int, GridDirection, int), int>();

        // The straight-run length is part of the search state: a cheap arrival
        // with a short run must not shadow a costlier arrival with a long run,
        // because only the latter may be allowed to turn (IsTurnValid).
        // Runs are capped at the largest value IsTurnValid ever requires.
        int runCap = _costCalculator.MinStraightRunCells;
        (int, int, GridDirection, int) StateKey(AStarNode n) =>
            (n.X, n.Y, n.Direction, Math.Min(n.StraightRunLength, runCap));

        // Create start node
        // StraightRunLength = 0 forces the path to go straight first before turning
        // This ensures waveguides exit components properly before bending
        var startNode = new AStarNode(startX, startY, startDirection)
        {
            GCost = 0,
            StraightRunLength = 0
        };
        startNode.HCost = _costCalculator.CalculateHeuristic(
            startX, startY, startDirection, endX, endY, endDirection);

        openSet.Enqueue(startNode, startNode.FCost);
        visited[StateKey(startNode)] = startNode;
        distanceFromStart[StateKey(startNode)] = 0;

        int nodesExpanded = 0;

        while (openSet.Count > 0 && nodesExpanded < MaxNodesExpanded)
        {
            // Check cancellation periodically to remain responsive
            if (nodesExpanded % CancellationCheckInterval == 0 && cancellationToken.IsCancellationRequested)
                return null;

            var current = openSet.Dequeue();
            nodesExpanded++;

            // Check if we reached the goal
            if (IsGoalReached(current, endX, endY, endDirection))
            {
                return ReconstructPath(current);
            }

            // Expand neighbors
            foreach (var neighbor in GetNeighbors(current, endX, endY, endDirection,
                                                   distanceFromStart, StateKey))
            {
                var key = StateKey(neighbor);

                if (visited.TryGetValue(key, out var existingNode))
                {
                    // Skip if we've found a better path already
                    if (neighbor.GCost >= existingNode.GCost)
                        continue;
                }

                visited[key] = neighbor;
                openSet.Enqueue(neighbor, neighbor.FCost);
            }
        }

        // No path found
        return null;
    }

    /// <summary>
    /// Checks if the current node has reached the goal.
    /// The node must be ON the pin's entry axis (zero perpendicular offset) —
    /// tolerance applies only along the approach direction. This prevents the
    /// octile search from landing laterally offset next to the pin, which the
    /// path smoother could not correct so close to the terminal.
    /// </summary>
    private bool IsGoalReached(AStarNode node, int endX, int endY, GridDirection endDirection)
    {
        if (node.Direction != endDirection)
            return false;

        int dx = endX - node.X;
        int dy = endY - node.Y;
        if (dx == 0 && dy == 0)
            return true;

        var (ux, uy) = endDirection.GetDelta();

        // Perpendicular offset from the entry axis must be exactly zero
        int cross = dx * uy - dy * ux;
        if (cross != 0)
            return false;

        // Goal must lie ahead along the entry direction, within tolerance
        int along = dx * ux + dy * uy;
        if (along <= 0)
            return false;

        int cellsAhead = Math.Max(Math.Abs(dx), Math.Abs(dy));
        return cellsAhead <= GoalTolerance;
    }

    /// <summary>
    /// Gets valid neighboring nodes from the current position.
    /// </summary>
    private IEnumerable<AStarNode> GetNeighbors(AStarNode current,
                                                  int goalX, int goalY, GridDirection goalDir,
                                                  Dictionary<(int, int, GridDirection, int), int> distFromStart,
                                                  Func<AStarNode, (int, int, GridDirection, int)> stateKey)
    {
        // Get distance from start for pin escape enforcement
        int distanceFromStart = distFromStart.GetValueOrDefault(stateKey(current), 0);

        foreach (var dir in GridDirectionExtensions.GetAllDirections())
        {
            var (dx, dy) = dir.GetDelta();
            int newX = current.X + dx;
            int newY = current.Y + dy;

            // Check bounds and obstacles
            if (_grid.IsBlocked(newX, newY))
                continue;

            // Diagonal block check: a diagonal step is only allowed when BOTH
            // orthogonal neighbor cells are free, so the waveguide cannot
            // cut through a component corner.
            if (dir.IsDiagonal() &&
                (_grid.IsBlocked(current.X + dx, current.Y) ||
                 _grid.IsBlocked(current.X, current.Y + dy)))
                continue;

            // CRITICAL: Force pin escape - must travel minimum distance in start direction
            // before allowing ANY turn. This ensures waveguides exit components cleanly.
            if (distanceFromStart < _costCalculator.MinPinEscapeCells)
            {
                // Only allow movement in the original start direction
                if (dir != current.Direction)
                    continue;
            }

            // CRITICAL: Force pin arrival - must approach goal in the correct direction
            // for the last N cells. This ensures clean arrival at the end pin.
            int distanceToGoal = Math.Abs(newX - goalX) + Math.Abs(newY - goalY);
            if (distanceToGoal <= _costCalculator.MinPinEscapeCells)
            {
                // Only allow movement in the goal direction when near the end
                if (dir != goalDir)
                    continue;
            }

            // Check if turn is valid (minimum straight run)
            if (!_costCalculator.IsTurnValid(current, dir))
                continue;

            // Don't allow sharp turns: only ±45° and ±90° direction changes are
            // physically realizable bends. This also excludes 180° reversals.
            if (current.Direction != GridDirection.None &&
                Math.Abs(GridDirectionExtensions.GetTurnAngle(current.Direction, dir)) > MaxTurnAngleDegrees)
                continue;

            // Calculate costs (including proximity penalty for being near other waveguides
            // and pin reservation zones)
            double moveCost = _costCalculator.CalculateMoveCost(current, newX, newY, dir);
            double proximityCost = _costCalculator.CalculateProximityCost(_grid, newX, newY);
            double pinZoneCost = _costCalculator.CalculatePinZoneCost(_grid, newX, newY);
            double newGCost = current.GCost + moveCost + proximityCost + pinZoneCost;
            double newHCost = _costCalculator.CalculateHeuristic(
                newX, newY, dir, goalX, goalY, goalDir);

            var neighbor = new AStarNode(newX, newY, dir)
            {
                GCost = newGCost,
                HCost = newHCost,
                Parent = current,
                StraightRunLength = (current.Direction == dir)
                    ? current.StraightRunLength + 1
                    : 1
            };

            // Track distance from start for this neighbor
            distFromStart[stateKey(neighbor)] = distanceFromStart + 1;

            yield return neighbor;
        }
    }

    /// <summary>
    /// Reconstructs the path from end node back to start.
    /// </summary>
    private List<AStarNode> ReconstructPath(AStarNode endNode)
    {
        var path = new List<AStarNode>();
        var current = endNode;

        while (current != null)
        {
            path.Add(current);
            current = current.Parent;
        }

        path.Reverse();
        return path;
    }
}
