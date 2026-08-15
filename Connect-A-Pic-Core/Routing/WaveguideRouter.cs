using CAP_Core.Components.Core;
using CAP_Core.Routing.AStarPathfinder;

namespace CAP_Core.Routing;

/// <summary>
/// Routes waveguides between physical pins, generating path segments.
/// Uses A* pathfinding with Manhattan fallback.
/// </summary>
public partial class WaveguideRouter
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

    /// <summary>
    /// Bend-radius floor (µm) for ELECTRICAL (metal trace) connections, sourced from the
    /// process metal cross-section via <c>MetalRoutingSpecFactory</c> (issue #854). RF metal
    /// routing needs curved bends just like waveguides — often LARGER than the optical floor —
    /// so electrical pin pairs use this floor instead of
    /// <see cref="ProcessMinBendRadiusMicrometers"/>. Defaults to the RF fallback
    /// (3 × the default 10 µm trace width); 0 means no metal constraint.
    /// </summary>
    public double MetalProcessMinBendRadiusMicrometers { get; set; } =
        MetalRouting.MetalRoutingSpec.DefaultMinBendRadiusMicrometers;

    /// <summary>Tolerance (µm) below which two bend radii count as equal.</summary>
    private const double RadiusToleranceMicrometers = 1e-6;

    /// <summary>
    /// Pin-to-pin distance (µm) below which the two pins sit at the same point — a
    /// perfect abutment (gdsfactory-style touching cells, pins snapped together on the
    /// canvas). Matches the router's endpoint tolerance
    /// (<see cref="SameEndpointToleranceMicrometers"/> in the direct-route partial) and the
    /// import pipeline's degenerate-route threshold, so all three agree on what an abutment is.
    /// </summary>
    private const double AbutmentPinDistanceMicrometers = 1.0;

    /// <summary>
    /// Allowed bend radii in micrometers (foundry-style discrete values).
    /// If empty, any radius >= MinBendRadiusMicrometers is allowed.
    /// When set, smoothing snaps each bend to the smallest allowed radius that fits; a
    /// post-routing pass (<see cref="AStarPathfinder.BendRadiusUpsizer"/>) then grows optical
    /// bends to the LARGEST allowed radius the free space around them permits (larger radii
    /// mean lower bend loss), once every sibling route is final.
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
    /// Routing policy (issue #860): when true, <see cref="Route"/> first tries the DIRECT
    /// styled geometry (straight / arc-S / sine / cobra per pin geometry, built by
    /// <see cref="InterconnectRouting.DirectRouteFirstPolicy"/>) and only falls back to A*
    /// when obstacles actually block the styled path. A* is never the first choice — it is
    /// the obstacle-avoidance fallback. Disable to force grid routing for every connection.
    /// </summary>
    public bool PreferDirectStyledRoutes { get; set; } = true;

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
    /// Routes a waveguide between two pins. With <see cref="PreferDirectStyledRoutes"/> (the
    /// default) the DIRECT styled geometry is tried first and A* only runs when obstacles
    /// actually block the styled path (issue #860). The A* attempt itself is two-phase.
    /// The first attempt honors the process bend-radius floor
    /// (<see cref="ProcessMinBendRadiusMicrometers"/>); when no clean path exists at the floor,
    /// it retries at the connection radius and marks the result with
    /// <see cref="RoutedPath.ViolatesProcessMinBendRadius"/>. Falls back to Manhattan routing
    /// if all A* attempts fail; a self-intersecting or blocked fallback at the floor radius
    /// is discarded in favor of the connection radius, and unresolvable results are marked
    /// <see cref="RoutedPath.IsBlockedFallback"/>.
    /// Pins closer together than <see cref="AbutmentPinDistanceMicrometers"/> short-circuit
    /// all of the above: they sit at the same point, so the route is a minimal butt joint —
    /// a valid, unflagged pin-to-pin straight.
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

        double abutmentDx = endX - startX;
        double abutmentDy = endY - startY;
        if (abutmentDx * abutmentDx + abutmentDy * abutmentDy
            < AbutmentPinDistanceMicrometers * AbutmentPinDistanceMicrometers)
        {
            return BuildAbutmentRoute(startX, startY, endX, endY);
        }

        double endInputAngle = AngleUtilities.NormalizeAngle(endAngle + 180);

        double connectionRadius = MinBendRadiusMicrometers;
        double effectiveRadius = Math.Max(connectionRadius, ProcessFloorFor(startPin, endPin));
        bool floorRaisesRadius = effectiveRadius > connectionRadius + RadiusToleranceMicrometers;

        if (PreferDirectStyledRoutes)
        {
            var direct = TryRouteDirect(startPin, endPin, effectiveRadius);
            if (direct != null)
                return direct;
        }

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
    /// The process bend-radius floor governing this pin pair: the metal floor for
    /// electrical pins (RF traces must bend at the metal cross-section radius),
    /// the optical floor otherwise. Checking one pin is authoritative — cross-kind
    /// pairs are rejected at connection creation time.
    /// </summary>
    private double ProcessFloorFor(PhysicalPin startPin, PhysicalPin endPin) =>
        startPin.MatterType == MatterType.Electricity || endPin.MatterType == MatterType.Electricity
            ? MetalProcessMinBendRadiusMicrometers
            : ProcessMinBendRadiusMicrometers;

    /// <summary>
    /// The route for a perfect abutment: both pins sit at the same point, so the honest
    /// geometry is a single (possibly zero-length) pin-to-pin straight — no bends, no
    /// fallback, no blocked flag. This is the same shape the GDS import pipeline builds
    /// for coincident-pin abutments.
    /// </summary>
    private static RoutedPath BuildAbutmentRoute(double startX, double startY, double endX, double endY)
    {
        double dx = endX - startX;
        double dy = endY - startY;
        double headingDegrees = dx != 0 || dy != 0
            ? AngleUtilities.NormalizeAngle(Math.Atan2(dy, dx) * 180.0 / Math.PI)
            : 0.0;
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(startX, startY, endX, endY, headingDegrees));
        return path;
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
            return DegradeToBlockedFallback(relaxed, startX, startY, endX, endY);
        }

        return DegradeToBlockedFallback(path, startX, startY, endX, endY);
    }

    /// <summary>
    /// Flags a fallback path as blocked. A fallback that merely grazes an obstacle keeps its
    /// geometry, but a self-crossing one (the loop/teardrop the CSC router produces for pins
    /// that face away from each other in tight quarters) has no optical model and must never
    /// reach export — it is replaced by an honest straight line between the pins, marked
    /// <see cref="RoutedPath.IsPlaceholderGeometry"/> so downstream consumers (export, most
    /// notably) know this is not real geometry, on top of the usual blocked flag that surfaces
    /// the connection as unroutable rather than drawn as a valid loop.
    /// </summary>
    private static RoutedPath DegradeToBlockedFallback(
        RoutedPath candidate, double startX, double startY, double endX, double endY)
    {
        if (PathIntersectionDetector.HasSelfIntersection(candidate))
        {
            double headingDegrees = AngleUtilities.NormalizeAngle(
                Math.Atan2(endY - startY, endX - startX) * 180.0 / Math.PI);
            candidate = new RoutedPath { IsPlaceholderGeometry = true };
            candidate.Segments.Add(new StraightSegment(startX, startY, endX, endY, headingDegrees));
        }
        candidate.IsBlockedFallback = true;
        return candidate;
    }

    /// <summary>Runs the Manhattan (CSC) router at the given bend radius.</summary>
    private static RoutedPath RouteManhattan(
        double startX, double startY, double startAngle,
        double endX, double endY, double endInputAngle, double bendRadius)
    {
        var path = new RoutedPath();
        // No lead-in/lead-out: the CSC route is tangential at both pins by construction,
        // so the first arc may begin directly at the start pin and the last arc may end
        // directly at the end pin. The old 15%-of-radius lead was a cosmetic relic that
        // forced a straight stub at every fallback pin (pin-lead-stub field finding).
        var manhattan = new ManhattanRouter(bendRadius);
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

}
