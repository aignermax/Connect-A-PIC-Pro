namespace CAP_Core.Routing.AStarPathfinder;

/// <summary>
/// Calculates movement costs and heuristics for A* pathfinding.
/// Encapsulates the cost model for waveguide routing.
/// </summary>
public class RoutingCostCalculator
{
    /// <summary>
    /// Cost per micrometer of straight travel.
    /// </summary>
    public double StraightCostPerMicrometer { get; set; } = 1.0;

    /// <summary>
    /// Cost penalty per 90-degree turn.
    /// Higher values prefer straighter paths with fewer bends.
    /// Default 50 means a 90° turn costs as much as 50µm of straight travel.
    /// </summary>
    public double TurnCostPer90Degrees { get; set; } = 50.0;

    /// <summary>
    /// Minimum bend radius in micrometers.
    /// Used to determine minimum straight run before turns.
    /// </summary>
    public double MinBendRadiusMicrometers { get; set; } = 10.0;

    /// <summary>
    /// Minimum number of cells to travel straight before allowing a turn.
    /// This ensures there's enough space for a proper bend.
    /// Typically 2x bend radius / cell size.
    /// </summary>
    public int MinStraightRunCells { get; set; } = 20;

    /// <summary>
    /// Minimum "escape distance" from start pin before allowing turns.
    /// This forces waveguides to exit components in the pin direction
    /// before routing toward the destination.
    /// <see cref="CAP_Core.Routing.WaveguideRouter"/> overrides this per routing attempt
    /// with the radius-derived tangent escape (bend radius rounded up past the next whole
    /// cell), so the first bend can begin directly at the pin — the default below only
    /// applies to direct users of the calculator.
    /// </summary>
    public int MinPinEscapeCells { get; set; } = 15;

    /// <summary>
    /// Grid cell size for cost calculations.
    /// </summary>
    public double CellSizeMicrometers { get; set; } = 1.0;

    /// <summary>
    /// Minimum safe spacing from other waveguides to prevent evanescent coupling.
    /// Typical values: 5-15 µm depending on waveguide design.
    /// Default: 10 µm (safe for most silicon photonics)
    /// </summary>
    public double MinSafeSpacingMicrometers { get; set; } = 10.0;

    /// <summary>
    /// Cost penalty multiplier for cells within the safe spacing zone.
    /// Higher values make A* prefer paths farther from obstacles.
    /// Default: 100 (makes proximity almost as expensive as a turn)
    /// </summary>
    public double ProximityCostMultiplier { get; set; } = 100.0;

    /// <summary>
    /// Cost penalty for routing through a pin reservation zone.
    /// Lower than ProximityCostMultiplier — a soft nudge, not a hard block.
    /// Default: 30 (roughly half a turn cost — enough to prefer a detour).
    /// </summary>
    public double PinZoneCostPenalty { get; set; } = 30.0;

    /// <summary>
    /// Precomputed distance transform for O(1) proximity cost lookups.
    /// When set, CalculateProximityCost uses this instead of brute-force scanning.
    /// </summary>
    public DistanceTransform? DistanceTransformGrid { get; set; }

    /// <summary>
    /// Calculates the cost to move from one node to an adjacent cell.
    /// </summary>
    /// <param name="from">Current node</param>
    /// <param name="toX">Target X coordinate</param>
    /// <param name="toY">Target Y coordinate</param>
    /// <param name="toDirection">Direction of movement to target</param>
    /// <returns>Movement cost</returns>
    public double CalculateMoveCost(AStarNode from, int toX, int toY, GridDirection toDirection)
    {
        // Base movement cost (distance): diagonal steps cover √2 × cell size
        double stepLength = toDirection.IsDiagonal() ? Sqrt2 * CellSizeMicrometers : CellSizeMicrometers;
        double cost = stepLength * StraightCostPerMicrometer;

        // Turn cost, proportional to the turn angle (a 45° turn costs half a 90° turn)
        if (from.Direction != GridDirection.None && from.Direction != toDirection)
        {
            double turnAngle = Math.Abs(GridDirectionExtensions.GetTurnAngle(from.Direction, toDirection));
            cost += (turnAngle / 90.0) * TurnCostPer90Degrees;
        }

        return cost;
    }

    /// <summary>
    /// Checks if a turn is valid (respects minimum straight run).
    /// A 45° turn needs less run-up than a 90° turn because the bend
    /// tangent length is r·tan(22.5°) ≈ 0.414·r instead of r.
    /// </summary>
    /// <param name="from">Current node</param>
    /// <param name="toDirection">Proposed direction</param>
    /// <returns>True if the turn is allowed</returns>
    public bool IsTurnValid(AStarNode from, GridDirection toDirection)
    {
        // First move or same direction is always valid
        if (from.Direction == GridDirection.None || from.Direction == toDirection)
        {
            return true;
        }

        // Check minimum straight run before turning (halved for gentle 45° turns)
        double turnAngle = Math.Abs(GridDirectionExtensions.GetTurnAngle(from.Direction, toDirection));
        int requiredRun = turnAngle <= GridDirectionExtensions.AngleStepDegrees
            ? (MinStraightRunCells + 1) / 2
            : MinStraightRunCells;
        return from.StraightRunLength >= requiredRun;
    }

    /// <summary>
    /// Square root of 2, the length ratio of a diagonal grid step.
    /// </summary>
    private static readonly double Sqrt2 = Math.Sqrt(2.0);

    /// <summary>
    /// Whether the search may use 45° diagonal moves. Must match the
    /// pathfinder's setting: it selects the distance metric of the heuristic
    /// (octile when diagonals are allowed, Manhattan otherwise — Manhattan is
    /// tighter and inadmissible-free for pure 4-direction movement).
    /// </summary>
    public bool UseDiagonals { get; set; } = true;

    /// <summary>
    /// Calculates heuristic cost from current position to goal.
    /// With diagonals: octile distance (admissible for 8-direction movement),
    /// h = ((dMax − dMin) + √2·dMin) · cellSize · straightCost. Without
    /// diagonals: Manhattan distance. Plus a conservative bend estimate that
    /// never overestimates the true cost.
    /// </summary>
    public double CalculateHeuristic(int fromX, int fromY, GridDirection fromDir,
                                      int toX, int toY, GridDirection toDir)
    {
        int dx = Math.Abs(toX - fromX);
        int dy = Math.Abs(toY - fromY);
        int dMin = Math.Min(dx, dy);
        int dMax = Math.Max(dx, dy);

        // Octile distance for 8-direction search, Manhattan for 4-direction
        double distance = UseDiagonals
            ? ((dMax - dMin) + Sqrt2 * dMin) * CellSizeMicrometers
            : (dx + dy) * CellSizeMicrometers;

        // Estimate turns needed
        double turnEstimate = 0;

        // Mixed displacement requires at least one turn: a 45° turn with
        // diagonals (use half its cost to stay safely admissible near the
        // goal tolerance), a full 90° turn without.
        if (dMin > 0 && dMax > dMin)
        {
            turnEstimate += TurnCostPer90Degrees * (UseDiagonals ? 0.25 : 0.5);
        }

        // If final direction doesn't match current direction, we may need another turn
        if (fromDir != GridDirection.None && fromDir != toDir)
        {
            double angleDiff = Math.Abs(GridDirectionExtensions.GetTurnAngle(fromDir, toDir));
            if (angleDiff > 0)
            {
                turnEstimate += (angleDiff / 90.0) * TurnCostPer90Degrees * 0.3;
            }
        }

        return distance * StraightCostPerMicrometer + turnEstimate;
    }

    /// <summary>
    /// Calculates proximity cost for a cell based on distance to nearest waveguide obstacle.
    /// Returns higher cost for cells within MinSafeSpacingMicrometers of other waveguides.
    /// This helps prevent evanescent coupling between adjacent waveguides.
    /// </summary>
    /// <param name="grid">The pathfinding grid</param>
    /// <param name="x">Cell X coordinate</param>
    /// <param name="y">Cell Y coordinate</param>
    /// <returns>Additional cost penalty (0 if far from obstacles)</returns>
    public double CalculateProximityCost(PathfindingGrid grid, int x, int y)
    {
        // Fast path: use precomputed distance transform (O(1) lookup)
        if (DistanceTransformGrid != null)
        {
            double dist = DistanceTransformGrid.GetDistanceMicrometers(x, y);
            if (dist >= MinSafeSpacingMicrometers) return 0;
            double proximityRatio = 1.0 - (dist / MinSafeSpacingMicrometers);
            return proximityRatio * ProximityCostMultiplier;
        }

        // Fallback: brute-force scan (O(N²) where N = search radius)
        return CalculateProximityCostBruteForce(grid, x, y);
    }

    /// <summary>
    /// Original brute-force proximity cost calculation.
    /// Scans all cells within MinSafeSpacingMicrometers radius.
    /// </summary>
    private double CalculateProximityCostBruteForce(PathfindingGrid grid, int x, int y)
    {
        int searchRadiusCells = (int)Math.Ceiling(MinSafeSpacingMicrometers / CellSizeMicrometers);

        double minDistance = double.MaxValue;
        bool foundWaveguide = false;

        for (int dx = -searchRadiusCells; dx <= searchRadiusCells; dx++)
        {
            for (int dy = -searchRadiusCells; dy <= searchRadiusCells; dy++)
            {
                if (dx == 0 && dy == 0) continue;

                int checkX = x + dx;
                int checkY = y + dy;

                byte cellState = grid.GetCellState(checkX, checkY);

                if (cellState == 2)
                {
                    double distance = Math.Sqrt(dx * dx + dy * dy) * CellSizeMicrometers;
                    if (distance < minDistance)
                    {
                        minDistance = distance;
                        foundWaveguide = true;
                    }
                }
            }
        }

        if (!foundWaveguide || minDistance >= MinSafeSpacingMicrometers)
            return 0;

        double proximityRatio = 1.0 - (minDistance / MinSafeSpacingMicrometers);
        return proximityRatio * ProximityCostMultiplier;
    }

    /// <summary>
    /// Returns a cost penalty if the cell is inside a pin reservation zone.
    /// This nudges routes away from pin areas, keeping them accessible.
    /// </summary>
    public double CalculatePinZoneCost(PathfindingGrid grid, int x, int y)
    {
        return grid.IsPinReservationZone(x, y) ? PinZoneCostPenalty : 0;
    }

    /// <summary>
    /// Creates a cost calculator with settings derived from routing parameters.
    /// </summary>
    public static RoutingCostCalculator FromRoutingParameters(
        double cellSize, double minBendRadius, double minSpacing)
    {
        return new RoutingCostCalculator
        {
            CellSizeMicrometers = cellSize,
            MinBendRadiusMicrometers = minBendRadius,
            MinStraightRunCells = (int)Math.Ceiling(minBendRadius * 2 / cellSize),
            MinSafeSpacingMicrometers = Math.Max(10.0, minSpacing * 2) // At least 10µm or 2x component spacing
        };
    }
}
