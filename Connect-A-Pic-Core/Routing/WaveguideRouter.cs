using CAP_Core.Components.Core;
using CAP_Core.Routing.AStarPathfinder;

namespace CAP_Core.Routing;

/// <summary>
/// Routes waveguides between physical pins, generating path segments.
/// Uses A* pathfinding with Manhattan fallback.
/// </summary>
public class WaveguideRouter
{
    /// <summary>
    /// The connection's minimum bend radius in micrometers. Violating this causes high loss.
    /// </summary>
    public double MinBendRadiusMicrometers { get; set; } = 10.0;

    /// <summary>
    /// Bend-radius floor (µm) imposed by the active fabrication process
    /// (<c>WaveguideBendRadiusResolver</c>). <see cref="Route"/> first attempts the larger of
    /// this floor and <see cref="MinBendRadiusMicrometers"/>; when no clean path exists at the
    /// floor it retries at the connection radius and marks the result with
    /// <see cref="RoutedPath.ViolatesProcessMinBendRadius"/> so the design checks surface the
    /// violation. 0 means no process constraint.
    /// </summary>
    public double ProcessMinBendRadiusMicrometers { get; set; }

    /// <summary>Tolerance (µm) below which two bend radii count as equal.</summary>
    private const double RadiusToleranceMicrometers = 1e-6;

    /// <summary>
    /// Allowed bend radii in micrometers (foundry-style discrete values).
    /// If empty, any radius >= MinBendRadiusMicrometers is allowed.
    /// When set, bends will snap to the smallest allowed radius that fits.
    /// </summary>
    public List<double> AllowedBendRadii { get; set; } = new() { 5, 10, 20, 50 };

    /// <summary>
    /// Minimum spacing between waveguides in micrometers.
    /// </summary>
    public double MinWaveguideSpacingMicrometers { get; set; } = 2.0;

    /// <summary>
    /// List of obstacles (component bounding boxes) to route around.
    /// </summary>
    public List<RoutingObstacle> Obstacles { get; } = new();

    /// <summary>
    /// The pathfinding grid for A* routing.
    /// </summary>
    public PathfindingGrid? PathfindingGrid { get; private set; }

    /// <summary>
    /// Cost calculator for A* routing.
    /// </summary>
    public RoutingCostCalculator CostCalculator { get; } = new();

    private HierarchicalPathfinder? _hierarchicalPathfinder;

    /// <summary>
    /// Grid cell size in micrometers for A* pathfinding.
    /// Larger values = faster routing but less precise obstacle avoidance.
    /// Recommended: 3-5µm for most designs.
    /// </summary>
    public double AStarCellSize { get; set; } = 4.0;

    /// <summary>
    /// Clearance padding around components in micrometers.
    /// </summary>
    public double ObstaclePaddingMicrometers { get; set; } = 5.0;

    /// <summary>
    /// Whether to use hierarchical pathfinding (HPA*) for long-distance routes.
    /// When false, uses flat A* with increased node limit for all routes.
    /// Set to false if experiencing routing detours or loops.
    /// </summary>
    public bool UseHierarchicalPathfinding { get; set; } = false;

    /// <summary>
    /// Whether the A* search may use 45° diagonal moves (octile routing).
    /// Diagonals yield shorter, more compact routes but roughly double the
    /// per-cell state space, making every re-route noticeably more expensive.
    /// Library default is true; the application layer decides the product
    /// default (currently opt-in via the Routing settings page).
    /// </summary>
    public bool UseDiagonalRouting { get; set; } = true;

    /// <summary>
    /// Maximum nodes for Phase 1 (quick) pathfinding.
    /// Phase 1 provides fast results for simple and medium-complexity routes.
    /// </summary>
    public int Phase1MaxNodes { get; set; } = 200_000;

    /// <summary>
    /// Maximum nodes for Phase 2 (extended) pathfinding.
    /// Phase 2 is triggered when Phase 1 fails, allowing longer search for complex routes.
    /// </summary>
    public int Phase2MaxNodes { get; set; } = 2_000_000;

    /// <summary>
    /// Invoked when routing escalates to Phase 2 (complex path search).
    /// Use this to show a "Computing complex path..." indicator in the UI.
    /// Called on the routing thread (not the UI thread).
    /// </summary>
    public Action? OnComplexRouteStarted { get; set; }

    /// <summary>
    /// Initializes the pathfinding grid for A* routing.
    /// </summary>
    public void InitializePathfindingGrid(double minX, double minY, double maxX, double maxY,
                                           IEnumerable<Component> components,
                                           double? cellSize = null)
    {
        double size = cellSize ?? AStarCellSize;
        PathfindingGrid = new PathfindingGrid(minX, minY, maxX, maxY, size, ObstaclePaddingMicrometers);
        PathfindingGrid.RebuildFromComponents(components);

        CostCalculator.CellSizeMicrometers = size;
        CostCalculator.MinBendRadiusMicrometers = MinBendRadiusMicrometers;
        CostCalculator.MinStraightRunCells = (int)Math.Ceiling(MinBendRadiusMicrometers * 2 / size);

        CostCalculator.DistanceTransformGrid = null;
    }

    /// <summary>
    /// Clears the pathfinding grid and hierarchical pathfinder.
    /// Used for test cleanup to prevent shared state pollution.
    /// </summary>
    public void ClearPathfindingGrid()
    {
        PathfindingGrid = null;
        _hierarchicalPathfinder = null;
        CostCalculator.DistanceTransformGrid = null;
    }

    /// <summary>
    /// Builds the hierarchical pathfinding graph for fast long-distance routing.
    /// Only builds if UseHierarchicalPathfinding is enabled.
    /// </summary>
    public void BuildHierarchicalGraph(int sectorSizeCells = 50)
    {
        if (PathfindingGrid == null) return;

        if (!UseHierarchicalPathfinding)
        {
            // HPA* disabled - clear any existing hierarchical pathfinder
            _hierarchicalPathfinder = null;
            CostCalculator.DistanceTransformGrid = null;
            return;
        }

        _hierarchicalPathfinder = new HierarchicalPathfinder(PathfindingGrid, CostCalculator);
        _hierarchicalPathfinder.BuildSectorGraph(sectorSizeCells);
        CostCalculator.DistanceTransformGrid = _hierarchicalPathfinder.DistanceTransform;

        // Wire DT incremental updates via PathfindingGrid callbacks
        var dt = _hierarchicalPathfinder.DistanceTransform;
        var grid = PathfindingGrid;
        grid.OnWaveguideCellsAdded = cells => dt?.AddWaveguideCells(cells);
        grid.OnAllWaveguidesCleared = () => dt?.Rebuild(grid);
    }

    /// <summary>
    /// Rebuilds only the distance transform (after waveguide changes).
    /// </summary>
    public void RebuildDistanceTransform()
    {
        if (PathfindingGrid == null || _hierarchicalPathfinder?.DistanceTransform == null) return;
        _hierarchicalPathfinder.DistanceTransform.Rebuild(PathfindingGrid);
    }

    public void UpdateComponentObstacle(Component component) =>
        PathfindingGrid?.UpdateComponentObstacle(component);

    public void RemoveComponentObstacle(Component component) =>
        PathfindingGrid?.RemoveComponentObstacle(component);

    public void AddComponentObstacle(Component component) =>
        PathfindingGrid?.AddComponentObstacle(component);

    /// <summary>
    /// Routes a waveguide between two pins using two-phase A* pathfinding.
    /// The first attempt honors the process bend-radius floor
    /// (<see cref="ProcessMinBendRadiusMicrometers"/>); when no clean path exists at the floor,
    /// it retries at the connection radius and marks the result with
    /// <see cref="RoutedPath.ViolatesProcessMinBendRadius"/>. Falls back to Manhattan routing
    /// if all A* attempts fail; a self-intersecting or blocked fallback at the floor radius
    /// is discarded in favor of the connection radius, and unresolvable results are marked
    /// <see cref="RoutedPath.IsBlockedFallback"/>.
    /// </summary>
    /// <param name="startPin">Source pin.</param>
    /// <param name="endPin">Target pin.</param>
    /// <param name="cancellationToken">Token to cancel Phase 2 (e.g. when grid changes).</param>
    public RoutedPath Route(PhysicalPin startPin, PhysicalPin endPin,
                             CancellationToken cancellationToken = default)
    {
        var (startX, startY) = startPin.GetAbsolutePosition();
        var (endX, endY) = endPin.GetAbsolutePosition();
        double startAngle = startPin.GetAbsoluteAngle();
        double endAngle = endPin.GetAbsoluteAngle();

        double endInputAngle = AngleUtilities.NormalizeAngle(endAngle + 180);

        double connectionRadius = MinBendRadiusMicrometers;
        double effectiveRadius = Math.Max(connectionRadius, ProcessMinBendRadiusMicrometers);
        bool floorRaisesRadius = effectiveRadius > connectionRadius + RadiusToleranceMicrometers;

        if (PathfindingGrid != null)
        {
            var astarPath = new RoutedPath();
            if (TryRouteAStar(effectiveRadius, startX, startY, startAngle, endX, endY, endInputAngle,
                              astarPath, startPin, endPin, cancellationToken)
                && IsCleanAStarResult(astarPath))
            {
                return astarPath;
            }

            // Controlled degradation: the process floor found no clean path — retry at the
            // connection radius and surface the violation instead of degenerate geometry.
            if (floorRaisesRadius)
            {
                astarPath = new RoutedPath();
                if (TryRouteAStar(connectionRadius, startX, startY, startAngle, endX, endY, endInputAngle,
                                  astarPath, startPin, endPin, cancellationToken)
                    && IsCleanAStarResult(astarPath))
                {
                    astarPath.ViolatesProcessMinBendRadius = true;
                    return astarPath;
                }
            }
        }

        return RouteManhattanFallback(startX, startY, startAngle, endX, endY, endInputAngle,
                                      connectionRadius, effectiveRadius, floorRaisesRadius);
    }

    /// <summary>
    /// Manhattan (CSC) fallback when all A* attempts fail. Tries the process floor radius
    /// first; a result that loops through itself or crosses obstacles is discarded in favor
    /// of the connection radius (marked as a process-minimum violation). A result that is
    /// still blocked or self-intersecting is marked <see cref="RoutedPath.IsBlockedFallback"/>.
    /// </summary>
    private RoutedPath RouteManhattanFallback(
        double startX, double startY, double startAngle,
        double endX, double endY, double endInputAngle,
        double connectionRadius, double effectiveRadius, bool floorRaisesRadius)
    {
        var path = RouteManhattan(startX, startY, startAngle, endX, endY, endInputAngle, effectiveRadius);
        if (IsCleanFallback(path)) return path;

        if (floorRaisesRadius)
        {
            var relaxed = RouteManhattan(startX, startY, startAngle, endX, endY, endInputAngle, connectionRadius);
            relaxed.ViolatesProcessMinBendRadius = true;
            if (IsCleanFallback(relaxed)) return relaxed;

            // Both radii failed — keep the tighter (shorter, less loop-prone) geometry.
            relaxed.IsBlockedFallback = true;
            return relaxed;
        }

        path.IsBlockedFallback = true;
        return path;
    }

    /// <summary>Runs the Manhattan (CSC) router at the given bend radius.</summary>
    private static RoutedPath RouteManhattan(
        double startX, double startY, double startAngle,
        double endX, double endY, double endInputAngle, double bendRadius)
    {
        var path = new RoutedPath();
        // Use small lead-in/lead-out for smoother transitions (15% of bend radius)
        double leadLength = bendRadius * 0.15;
        var manhattan = new ManhattanRouter(bendRadius, leadOut: leadLength, leadIn: leadLength);
        manhattan.Route(startX, startY, startAngle, endX, endY, endInputAngle, path);
        return path;
    }

    /// <summary>
    /// An A* result is accepted only when its segments connect and the smoothed geometry
    /// does not intersect itself (arcs can drift when the grid path is tighter than planned).
    /// </summary>
    private static bool IsCleanAStarResult(RoutedPath path) =>
        path.IsValid && !PathIntersectionDetector.HasSelfIntersection(path);

    /// <summary>
    /// A fallback path is acceptable as-is only when it has connected segments, does not
    /// pass through obstacles, and does not intersect itself (no loops/teardrops).
    /// </summary>
    private bool IsCleanFallback(RoutedPath path) =>
        path.Segments.Count > 0
        && path.IsValid
        && !IsPathBlocked(path.Segments)
        && !PathIntersectionDetector.HasSelfIntersection(path);

    /// <summary>
    /// Checks if any segment in a path passes through blocked cells.
    /// </summary>
    public bool IsPathBlocked(IEnumerable<PathSegment> segments)
    {
        if (PathfindingGrid == null) return false;

        foreach (var segment in segments)
        {
            if (segment is StraightSegment)
            {
                if (IsLineBlocked(segment.StartPoint.X, segment.StartPoint.Y,
                                  segment.EndPoint.X, segment.EndPoint.Y))
                    return true;
            }
            else if (segment is BendSegment bend)
            {
                if (IsArcBlocked(bend)) return true;
            }
        }
        return false;
    }

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

    /// <summary>
    /// Checks if a straight line passes through any blocked cells.
    /// </summary>
    private bool IsLineBlocked(double x1, double y1, double x2, double y2)
    {
        if (PathfindingGrid == null) return false;

        double dx = x2 - x1;
        double dy = y2 - y1;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 0.001) return false;

        dx /= length;
        dy /= length;

        double stepSize = PathfindingGrid.CellSizeMicrometers * 0.5;
        double margin = PathfindingGrid.CellSizeMicrometers;

        for (double t = margin; t < length - margin; t += stepSize)
        {
            double px = x1 + dx * t;
            double py = y1 + dy * t;
            var (gx, gy) = PathfindingGrid.PhysicalToGrid(px, py);
            if (PathfindingGrid.IsBlocked(gx, gy)) return true;
        }
        return false;
    }

    /// <summary>
    /// Checks if an arc segment passes through blocked cells.
    /// </summary>
    private bool IsArcBlocked(BendSegment bend)
    {
        if (PathfindingGrid == null) return false;

        double startRad = bend.StartAngleDegrees * Math.PI / 180;
        double sweepRad = bend.SweepAngleDegrees * Math.PI / 180;
        double arcLength = Math.Abs(sweepRad) * bend.RadiusMicrometers;
        double stepLength = PathfindingGrid.CellSizeMicrometers * 0.5;
        int numSamples = Math.Max(10, (int)Math.Ceiling(arcLength / stepLength));

        double sign = Math.Sign(bend.SweepAngleDegrees);
        if (sign == 0) sign = 1;

        for (int i = 1; i < numSamples; i++)
        {
            double t = (double)i / numSamples;
            double angle = startRad + sweepRad * t;
            double px = bend.Center.X + bend.RadiusMicrometers * Math.Cos(angle - Math.PI / 2 * sign);
            double py = bend.Center.Y + bend.RadiusMicrometers * Math.Sin(angle - Math.PI / 2 * sign);

            var (gx, gy) = PathfindingGrid.PhysicalToGrid(px, py);
            if (PathfindingGrid.IsBlocked(gx, gy)) return true;
        }
        return false;
    }
}
