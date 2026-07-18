using CAP_Core.Components.Core;
using CAP_Core.Routing.AStarPathfinder;

namespace CAP_Core.Routing;

/// <summary>
/// The A*-attempt half of <see cref="WaveguideRouter"/>: one obstacle-avoiding routing try at
/// a specific bend radius, with the cost model, pin corridors and path smoother synchronized
/// to that radius (split out to keep the router below the file-size limit).
/// </summary>
public partial class WaveguideRouter
{
    /// <summary>
    /// Attempts to route using two-phase A* pathfinding with obstacle avoidance at the given
    /// bend radius. The cost model (minimum straight run before turns) and the path smoother
    /// are synced to that radius, so the grid path leaves room for the arcs that will be built.
    /// Phase 1 uses <see cref="Phase1MaxNodes"/> for fast results.
    /// Phase 2 uses <see cref="Phase2MaxNodes"/> and fires <see cref="OnComplexRouteStarted"/> if Phase 1 fails.
    /// </summary>
    private bool TryRouteAStar(double bendRadius,
                                double startX, double startY, double startAngle,
                                double endX, double endY, double endInputAngle,
                                RoutedPath path, PhysicalPin startPin, PhysicalPin endPin,
                                CancellationToken cancellationToken = default)
    {
        if (PathfindingGrid == null) return false;

        // Sync the cost model to the radius of THIS attempt: turns must be spaced far
        // enough apart that the smoother can realize them as arcs of this radius.
        CostCalculator.MinBendRadiusMicrometers = bendRadius;
        CostCalculator.MinStraightRunCells =
            (int)Math.Ceiling(bendRadius * 2 / PathfindingGrid.CellSizeMicrometers);

        double corridorLength = bendRadius * 3;
        double corridorWidth = bendRadius;

        var clearedStart = PathfindingGrid.ClearPinCorridor(
            startX, startY, startAngle, corridorLength, corridorWidth);

        // Clear corridors in BOTH directions for the end pin:
        // 1. Facing direction (away from component) — ensures approach path is clear
        // 2. Input direction (into component) — ensures the terminal grid cell is reachable
        double endFacingAngle = AngleUtilities.NormalizeAngle(endInputAngle + 180);
        var clearedEndApproach = PathfindingGrid.ClearPinCorridor(
            endX, endY, endFacingAngle, corridorLength, corridorWidth);
        var clearedEndTerminal = PathfindingGrid.ClearPinCorridor(
            endX, endY, endInputAngle, corridorLength, corridorWidth);

        try
        {
            var (gridStartX, gridStartY) = PathfindingGrid.PhysicalToGrid(startX, startY);
            var (gridEndX, gridEndY) = PathfindingGrid.PhysicalToGrid(endX, endY);

            var startDir = GridDirectionExtensions.FromAngle(startAngle);
            var endDir = GridDirectionExtensions.FromAngle(endInputAngle);

            int originalEscapeCells = CostCalculator.MinPinEscapeCells;

            // Scale escape distance based on pin separation.
            // Both start escape + end approach must fit within the total distance,
            // with room left for turns. Use 1/6 of distance, minimum 2 cells.
            int gridDistance = Math.Abs(gridEndX - gridStartX) + Math.Abs(gridEndY - gridStartY);
            int scaledEscape = Math.Min(originalEscapeCells, Math.Max(2, gridDistance / 6));
            CostCalculator.MinPinEscapeCells = scaledEscape;

            // Also scale MinStraightRunCells for close pins to allow tighter turns
            int originalStraightRun = CostCalculator.MinStraightRunCells;
            int scaledStraightRun = Math.Min(originalStraightRun, Math.Max(2, gridDistance / 4));
            CostCalculator.MinStraightRunCells = scaledStraightRun;

            List<AStarNode>? gridPath = null;

            // The heuristic's distance metric must match the movement model.
            CostCalculator.UseDiagonals = UseDiagonalRouting;

            if (_hierarchicalPathfinder != null && UseHierarchicalPathfinding)
            {
                gridPath = _hierarchicalPathfinder.FindPath(
                    gridStartX, gridStartY, startDir,
                    gridEndX, gridEndY, endDir);
            }
            else
            {
                // Phase 1: Quick search with limited node budget for fast results
                var phase1 = new AStarPathfinder.AStarPathfinder(PathfindingGrid, CostCalculator)
                {
                    MaxNodesExpanded = Phase1MaxNodes,
                    UseDiagonals = UseDiagonalRouting
                };
                gridPath = phase1.FindPath(gridStartX, gridStartY, startDir,
                                           gridEndX, gridEndY, endDir, cancellationToken);

                // Phase 2: Extended search when Phase 1 exhausted its node budget
                if (gridPath == null && !cancellationToken.IsCancellationRequested)
                {
                    OnComplexRouteStarted?.Invoke();
                    var phase2 = new AStarPathfinder.AStarPathfinder(PathfindingGrid, CostCalculator)
                    {
                        MaxNodesExpanded = Phase2MaxNodes,
                        UseDiagonals = UseDiagonalRouting
                    };
                    gridPath = phase2.FindPath(gridStartX, gridStartY, startDir,
                                               gridEndX, gridEndY, endDir, cancellationToken);
                }
            }

            // Lateral-tolerance retry: the strict phases require an exact
            // on-axis arrival, which is impossible when another waveguide
            // crosses the pin's entry axis outside the cleared corridor.
            // Retry accepting a small lateral offset; the smoother snaps the
            // final approach onto the axis. Only otherwise-blocked routes
            // reach this point, so successful routes are unaffected.
            if (gridPath == null && !cancellationToken.IsCancellationRequested)
            {
                var tolerantRetry = new AStarPathfinder.AStarPathfinder(PathfindingGrid, CostCalculator)
                {
                    MaxNodesExpanded = Phase1MaxNodes,
                    AllowLateralGoalTolerance = true,
                    UseDiagonals = UseDiagonalRouting
                };
                gridPath = tolerantRetry.FindPath(gridStartX, gridStartY, startDir,
                                                  gridEndX, gridEndY, endDir, cancellationToken);
            }

            // Loop detection: if path is >2× Manhattan distance, retry with minimal constraints
            if (gridPath != null && gridPath.Count > gridDistance * 2 && scaledEscape > 2)
            {
                CostCalculator.MinPinEscapeCells = 2;
                CostCalculator.MinStraightRunCells = 2;
                var retry = new AStarPathfinder.AStarPathfinder(PathfindingGrid, CostCalculator)
                {
                    UseDiagonals = UseDiagonalRouting
                };
                var retryPath = retry.FindPath(gridStartX, gridStartY, startDir,
                                               gridEndX, gridEndY, endDir, cancellationToken);
                if (retryPath != null && retryPath.Count < gridPath.Count)
                    gridPath = retryPath;
            }

            if (gridPath == null || gridPath.Count < 2)
            {
                CostCalculator.MinPinEscapeCells = 2;
                CostCalculator.MinStraightRunCells = 2;
                var fallback = new AStarPathfinder.AStarPathfinder(PathfindingGrid, CostCalculator)
                {
                    UseDiagonals = UseDiagonalRouting
                };
                gridPath = fallback.FindPath(gridStartX, gridStartY, startDir,
                                             gridEndX, gridEndY, endDir, cancellationToken);
            }

            CostCalculator.MinStraightRunCells = originalStraightRun;

            CostCalculator.MinPinEscapeCells = originalEscapeCells;

            if (gridPath == null || gridPath.Count < 2) return false;

            var smoother = new PathSmoother(PathfindingGrid, bendRadius, AllowedRadiiIncluding(bendRadius));
            var smoothedPath = smoother.ConvertToSegments(gridPath, startPin, endPin);

            path.Segments.AddRange(smoothedPath.Segments);
            path.IsInvalidGeometry = smoothedPath.IsInvalidGeometry;
            path.DebugGridPath = gridPath;

            // Success requires valid segments without geometry violations
            return path.Segments.Count > 0 && !path.IsInvalidGeometry;
        }
        finally
        {
            PathfindingGrid.RestoreCells(clearedStart);
            PathfindingGrid.RestoreCells(clearedEndApproach);
            PathfindingGrid.RestoreCells(clearedEndTerminal);
        }
    }

    /// <summary>
    /// The allowed bend radii extended with the current attempt's radius, so the smoother
    /// builds arcs of exactly that radius instead of snapping up to the next foundry value
    /// (which would not match the setbacks the grid path was planned with).
    /// </summary>
    private List<double> AllowedRadiiIncluding(double bendRadius)
    {
        if (AllowedBendRadii.Count == 0 ||
            AllowedBendRadii.Any(r => Math.Abs(r - bendRadius) < RadiusToleranceMicrometers))
            return AllowedBendRadii;

        var radii = new List<double>(AllowedBendRadii) { bendRadius };
        radii.Sort();
        return radii;
    }
}
