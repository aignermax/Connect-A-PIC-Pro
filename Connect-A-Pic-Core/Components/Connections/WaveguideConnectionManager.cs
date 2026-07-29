using System.Numerics;
using CAP_Core.Routing;
using CAP_Core.Routing.CrossingInsertion;
using CAP_Core.Components.Core;

namespace CAP_Core.Components.Connections;

public partial class WaveguideConnectionManager
{
    private readonly WaveguideRouter _router;

    /// <summary>
    /// Initializes a new instance of <see cref="WaveguideConnectionManager"/> with the given router.
    /// </summary>
    /// <param name="router">The waveguide router used for path calculation and obstacle management.</param>
    public WaveguideConnectionManager(WaveguideRouter router)
    {
        _router = router;
    }

    public List<WaveguideConnection> Connections { get; } = new();

    /// <summary>
    /// Guards <see cref="Connections"/> against the routing race (round-5 review [3]):
    /// UI-thread commands (ungroup, group-edit, undo/redo) mutate the list while a
    /// fire-and-forget routing pass enumerates it on a Task.Run thread — a plain List
    /// enumeration then throws "Collection was modified" and the pass dies with an
    /// unobserved exception. Cancel-and-await before mutating is NOT an option: the
    /// commands are synchronous (IUndoableCommand.Execute) and the routing semaphore is
    /// released on a UI-thread continuation, so blocking the UI thread would deadlock.
    /// Smallest robust fix: every mutation of the list takes this lock, and the routing
    /// passes enumerate a snapshot taken under it. A pass may then work on a momentarily
    /// stale set — harmless, because every mutating command ends with
    /// RecalculateRoutesAsync, which cancels the stale pass and starts a fresh one.
    /// </summary>
    private readonly object _connectionsSync = new();

    /// <summary>
    /// Guards structural mutations of <see cref="Connections"/> that happen on the
    /// routing thread (crossing insertion / dissolution) against UI-thread consumers
    /// that enumerate the list (S-matrix building, save, drop). Aliases the mutation
    /// lock so the crossing pass and the UI commands share ONE lock object.
    /// </summary>
    public object SyncRoot => _connectionsSync;

    /// <summary>Copy of <see cref="Connections"/> taken under the mutation lock.</summary>
    private List<WaveguideConnection> SnapshotConnections()
    {
        lock (_connectionsSync)
        {
            return Connections.ToList();
        }
    }

    /// <summary>
    /// Default propagation loss applied to new connections (dB/cm).
    /// Default: 0.5 dB/cm (high-quality strip waveguide)
    /// </summary>
    public double DefaultPropagationLossDbPerCm { get; set; } = 0.5;

    /// <summary>
    /// Default bend loss applied to new connections (dB per 90° bend).
    /// </summary>
    public double DefaultBendLossDbPer90Deg { get; set; } = 0.05;

    /// <summary>
    /// Whether to use sequential routing with waveguide collision avoidance.
    /// When enabled, already-routed waveguides are marked as obstacles for subsequent routes.
    /// </summary>
    public bool UseSequentialRouting { get; set; } = true;

    /// <summary>
    /// Waveguide width for collision detection (in micrometers).
    /// This is the waveguide core width plus minimum spacing on each side.
    /// Typical: 0.5µm core + 2µm clearance on each side = ~4.5µm total.
    /// </summary>
    public double WaveguideWidthMicrometers { get; set; } = 4.0;

    /// <summary>
    /// Optional adaptive crossing-insertion service. When set, a crossing pass runs
    /// after every routing pass and detours may be replaced by real PDK crossing
    /// components (see <see cref="CrossingInsertionService"/>). Null (default) keeps
    /// the classic avoid-only behavior.
    /// </summary>
    public CrossingInsertionService? CrossingInsertion { get; set; }

    /// <summary>Re-entrancy guard: sub-connection recalcs must not trigger nested crossing passes.</summary>
    private bool _isCrossingPassRunning;

    public WaveguideConnection AddConnection(PhysicalPin startPin, PhysicalPin endPin)
    {
        var connection = new WaveguideConnection
        {
            StartPin = startPin,
            EndPin = endPin,
            PropagationLossDbPerCm = DefaultPropagationLossDbPerCm,
            BendLossDbPer90Deg = DefaultBendLossDbPer90Deg
        };
        lock (_connectionsSync)
        {
            Connections.Add(connection);
        }

        // Recalculate ALL connections sequentially so the new one avoids existing waveguides
        // and existing ones are properly registered in the grid
        RecalculateAllTransmissions();

        return connection;
    }

    /// <summary>
    /// Adds a connection with a pre-computed cached route, bypassing A* routing.
    /// Registers the cached route as an obstacle in the pathfinding grid.
    /// Used when loading designs with cached route data.
    /// </summary>
    public WaveguideConnection AddConnectionWithCachedRoute(
        PhysicalPin startPin,
        PhysicalPin endPin,
        RoutedPath cachedPath)
    {
        var connection = new WaveguideConnection
        {
            StartPin = startPin,
            EndPin = endPin,
            PropagationLossDbPerCm = DefaultPropagationLossDbPerCm,
            BendLossDbPer90Deg = DefaultBendLossDbPer90Deg
        };

        connection.RestoreCachedPath(cachedPath);
        lock (_connectionsSync)
        {
            Connections.Add(connection);
        }

        // Register cached route as obstacle for future routing
        var router = _router;
        if (UseSequentialRouting && router.PathfindingGrid != null &&
            connection.IsPathValid && connection.RoutedPath != null)
        {
            router.PathfindingGrid.AddWaveguideObstacle(
                connection.Id,
                connection.RoutedPath.Segments,
                WaveguideWidthMicrometers);
        }

        return connection;
    }

    /// <summary>
    /// Adds a connection without triggering route calculation.
    /// Used for async routing: add connection first, then route asynchronously.
    /// </summary>
    public WaveguideConnection AddConnectionDeferred(PhysicalPin startPin, PhysicalPin endPin)
    {
        var connection = new WaveguideConnection
        {
            StartPin = startPin,
            EndPin = endPin,
            PropagationLossDbPerCm = DefaultPropagationLossDbPerCm,
            BendLossDbPer90Deg = DefaultBendLossDbPer90Deg
        };
        lock (_connectionsSync)
        {
            Connections.Add(connection);
        }
        return connection;
    }

    /// <summary>
    /// Asynchronously recalculates all transmissions on a background thread.
    /// Returns false if cancelled before completion. Never throws on cancellation.
    /// </summary>
    public async Task<bool> RecalculateAllTransmissionsAsync(
        Action? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return false;

        return await Task.Run(() =>
        {
            if (cancellationToken.IsCancellationRequested) return false;
            RecalculateAllTransmissions(progressCallback, cancellationToken);
            return !cancellationToken.IsCancellationRequested;
        });
    }

    /// <summary>
    /// Removes all connections for a component without triggering route recalculation.
    /// Caller is responsible for triggering RecalculateAllTransmissionsAsync().
    /// </summary>
    public void RemoveConnectionsForComponent(Component component)
    {
        // Dissolve any crossings touching this component first so their crossing
        // components and sub-connections are cleaned up (no orphaned crossings).
        CrossingInsertion?.DissolveForComponent(component, this, _router);

        var router = _router;
        var connectionsToRemove = SnapshotConnections()
            .Where(c => c.StartPin.ParentComponent == component ||
                        c.EndPin.ParentComponent == component)
            .ToList();

        // Remove waveguide obstacles for removed connections
        if (router.PathfindingGrid != null)
        {
            foreach (var conn in connectionsToRemove)
            {
                router.PathfindingGrid.RemoveWaveguideObstacle(conn.Id);
            }
        }

        lock (_connectionsSync)
        {
            Connections.RemoveAll(c =>
                c.StartPin.ParentComponent == component ||
                c.EndPin.ParentComponent == component);
        }
    }

    public void RemoveConnection(WaveguideConnection connection)
    {
        // If the connection is part of an inserted crossing, dissolve the crossing:
        // the crossing component and all four sub-connections are removed and the
        // other original connection is restored unsplit.
        if (CrossingInsertion != null &&
            CrossingInsertion.TryDissolveForConnection(connection, this, _router))
        {
            if (Connections.Count > 0)
            {
                RecalculateAllTransmissions();
            }
            return;
        }

        // Remove waveguide obstacle from pathfinding grid
        var router = _router;
        lock (_connectionsSync)
        {
            if (router.PathfindingGrid != null)
            {
                router.PathfindingGrid.RemoveWaveguideObstacle(connection.Id);
            }

            Connections.Remove(connection);
        }

        // Recalculate remaining connections - they might find better routes now
        if (Connections.Count > 0)
        {
            RecalculateAllTransmissions();
        }
    }

    /// <summary>
    /// Removes a connection without triggering route recalculation.
    /// Used for async routing: remove connection first, then route asynchronously.
    /// When the connection participates in an inserted crossing (as a sub-connection
    /// or as a split original), the crossing is dissolved instead: the crossing
    /// component and all four sub-connections are removed and the other original is
    /// restored unsplit — no delete path may leave an orphaned crossing behind.
    /// </summary>
    public void RemoveConnectionDeferred(WaveguideConnection connection)
    {
        if (CrossingInsertion != null &&
            CrossingInsertion.TryDissolveForConnection(connection, this, _router))
        {
            return;
        }

        var router = _router;
        lock (_connectionsSync)
        {
            if (router.PathfindingGrid != null)
            {
                router.PathfindingGrid.RemoveWaveguideObstacle(connection.Id);
            }
            Connections.Remove(connection);
        }
    }

    public void AddExistingConnection(WaveguideConnection connection)
    {
        lock (_connectionsSync)
        {
            if (!Connections.Contains(connection))
            {
                Connections.Add(connection);
            }
        }
    }

    public void Clear()
    {
        // Drop crossing bookkeeping with the design: stale records must never
        // resurrect dissolved originals into a fresh/loaded design (File → New,
        // project load, group-edit canvas swap).
        CrossingInsertion?.Reset();
        lock (_connectionsSync)
        {
            Connections.Clear();
        }
    }

    /// <summary>
    /// Maximum number of ordering permutations to try when routing fails.
    /// </summary>
    public int MaxRoutingAttempts { get; set; } = 6;

    /// <summary>
    /// Invoked on the routing thread when a connection escalates to Phase 2 (complex route).
    /// Wire this to update a UI progress indicator.
    /// </summary>
    public Action? OnComplexRouteStarted { get; set; }

    /// <summary>
    /// Recalculates transmission for all connections using incremental routing.
    /// Existing valid routes are preserved; only broken or new connections are re-routed.
    /// Falls back to full re-route if incremental routing leaves failed connections.
    /// </summary>
    /// <param name="progressCallback">Optional callback invoked after each connection is routed.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    public void RecalculateAllTransmissions(
        Action? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        // Re-evaluate existing crossings first: when a net endpoint moved, the
        // crossing is dissolved so its originals are routed from scratch below and
        // the insertion pass re-inserts a crossing only if it is still beneficial.
        if (CrossingInsertion != null && !_isCrossingPassRunning)
        {
            CrossingInsertion.DissolveStaleRecords(this, _router);
        }

        RouteAllConnections(progressCallback, cancellationToken);
        RunCrossingInsertionPass(cancellationToken);

        if (UseSequentialRouting && _router.PathfindingGrid != null &&
            !cancellationToken.IsCancellationRequested)
        {
            // Pull auto routes onto their pins now that every sibling is final, then flag any
            // crossing that even the collapse could not keep clear (there should be none). The
            // crossing scan must not run on a half-collapsed state if cancellation interrupted it.
            CollapseAutoRoutePinLeads(cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
                MarkUnresolvedSiblingCrossings();
        }
    }

    /// <summary>
    /// Runs the adaptive crossing-insertion pass after routing when a
    /// <see cref="CrossingInsertion"/> service is configured.
    /// </summary>
    private void RunCrossingInsertionPass(CancellationToken cancellationToken)
    {
        if (CrossingInsertion == null || !UseSequentialRouting || _router.PathfindingGrid == null) return;
        if (cancellationToken.IsCancellationRequested || _isCrossingPassRunning) return;

        _isCrossingPassRunning = true;
        try
        {
            CrossingInsertion.InsertBeneficialCrossings(this, _router, cancellationToken);
        }
        finally
        {
            _isCrossingPassRunning = false;
        }
    }

    /// <summary>
    /// Routes all connections (incremental first, then full re-route with ordering
    /// strategies) without running the crossing-insertion pass.
    /// </summary>
    private void RouteAllConnections(
        Action? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        var router = _router;

        // Wire the complex-route callback so Phase 2 escalations surface to the UI
        router.OnComplexRouteStarted = OnComplexRouteStarted;

        if (UseSequentialRouting && router.PathfindingGrid != null)
        {
            // Phase 1: Incremental routing — keep valid routes, only re-route broken ones
            var result = TryRouteIncremental(router, progressCallback, cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;

            if (result.allValid)
                return;

            // Phase 2: Incremental routing failed for some connections.
            // Fall back to full re-route with ordering strategies.
            // Snapshots: this runs on the routing thread while UI commands may mutate the list.
            result = TryRouteInOrder(SnapshotConnections(), router, progressCallback, cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;
            if (result.allValid) return;

            var bestOrder = SnapshotConnections();
            int bestFailedCount = result.failedCount;

            var orderings = GenerateOrderings(SnapshotConnections(), MaxRoutingAttempts - 1);
            foreach (var ordering in orderings)
            {
                if (cancellationToken.IsCancellationRequested) return;
                result = TryRouteInOrder(ordering, router, progressCallback, cancellationToken);

                if (result.allValid)
                {
                    ReorderConnections(ordering);
                    return;
                }

                if (result.failedCount < bestFailedCount)
                {
                    bestFailedCount = result.failedCount;
                    bestOrder = ordering;
                }
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                ReorderConnections(bestOrder);
                TryRouteInOrder(bestOrder, router, progressCallback, cancellationToken);
            }
        }
        else
        {
            // Simple routing without collision avoidance. Snapshot: see _connectionsSync.
            foreach (var connection in SnapshotConnections())
            {
                if (cancellationToken.IsCancellationRequested) return;
                connection.RecalculateTransmission(_router, cancellationToken: cancellationToken);
                progressCallback?.Invoke();
            }
        }
    }

    /// <summary>
    /// Tolerance for checking if path endpoints still match pin positions (in µm).
    /// </summary>
    private const double EndpointToleranceMicrometers = 1.0;

    /// <summary>
    /// Incremental routing: preserves existing valid routes, only re-routes broken or new connections.
    /// A route is "still valid" if:
    ///   1. It has a non-null, non-fallback RoutedPath
    ///   2. Its endpoints still match the current pin positions (component wasn't moved)
    ///   3. The path doesn't pass through any component obstacle
    /// </summary>
    private (bool allValid, int failedCount) TryRouteIncremental(
        WaveguideRouter router,
        Action? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        var grid = router.PathfindingGrid!;

        // After RebuildFromComponents, the grid has ONLY component obstacles (no waveguides).
        // Check each connection's existing route against component obstacles only.
        grid.ClearAllWaveguideObstacles();

        var validConnections = new List<WaveguideConnection>();
        var invalidConnections = new List<WaveguideConnection>();

        // Snapshot: this loop runs on the routing thread while UI commands (ungroup,
        // undo/redo) may add or remove connections — see _connectionsSync.
        foreach (var connection in SnapshotConnections())
        {
            if (cancellationToken.IsCancellationRequested)
                return (false, 0);

            // A joint drag of BOTH connected components is a pure translation: shift the
            // existing route (with all manual bend edits) to the new pin positions BEFORE
            // the validity checks, so collision handling below runs on the translated
            // geometry and can still force a re-route.
            JointMoveRouteTranslator.TryTranslateToPins(connection);

            if (TryUnfreezeCollidedAutoRoute(connection, router))
            {
                // A component overlaps the manually edited geometry: treat it like an
                // endpoint move — the edit is discarded and the connection re-routed.
                invalidConnections.Add(connection);
            }
            else if (IsRouteStillValid(connection, router))
            {
                RefreshStyledObstacleCollision(connection, router);
                validConnections.Add(connection);
            }
            else
            {
                invalidConnections.Add(connection);
            }
        }

        // Register all valid routes as waveguide obstacles first.
        // This ensures new/re-routed connections route around existing infrastructure.
        foreach (var connection in validConnections)
        {
            grid.AddWaveguideObstacle(
                connection.Id,
                connection.RoutedPath!.Segments,
                WaveguideWidthMicrometers);
        }

        // Route only the invalid/new connections
        int failedCount = 0;
        var routedSoFar = new List<WaveguideConnection>(validConnections);
        foreach (var connection in invalidConnections)
        {
            if (cancellationToken.IsCancellationRequested)
                return (false, failedCount);

            connection.RecalculateTransmission(_router, cancellationToken: cancellationToken);
            RefreshStyledObstacleCollision(connection, router);
            progressCallback?.Invoke();

            // Register ALL paths with valid geometry as obstacles, including blocked fallbacks.
            // This ensures all routes (A* and Manhattan) are visible to subsequent routing.
            if (connection.IsPathValid && connection.RoutedPath != null)
            {
                grid.AddWaveguideObstacle(
                    connection.Id,
                    connection.RoutedPath.Segments,
                    WaveguideWidthMicrometers);

                // A route that geometrically crosses a sibling counts as a failure so the
                // full re-route with ordering strategies gets a chance to untangle it.
                // Blocked fallbacks are otherwise NOT counted as failures here: they have
                // valid geometry and are registered as obstacles.
                if (CrossesAnyRoutedSibling(connection, routedSoFar))
                    failedCount++;
                routedSoFar.Add(connection);
            }
            else
            {
                // Only count connections with NO valid path as failures
                failedCount++;
            }
        }

        return (failedCount == 0, failedCount);
    }

    /// <summary>
    /// Checks if an existing route is still valid (not broken by component changes).
    /// </summary>
    private static bool IsRouteStillValid(WaveguideConnection connection, WaveguideRouter router)
    {
        // Frozen paths with matching endpoints are always kept as-is: manual bend edits
        // must survive re-routing. RecalculateTransmission handles the unfreeze case.
        if (connection.IsRouteFrozen && connection.FrozenPathStillMatchesPins())
            return true;

        if (connection.RoutedPath == null || !connection.IsPathValid)
            return false;

        if (connection.RoutedPath.IsBlockedFallback)
            return false;

        if (connection.RoutedPath.Segments.Count == 0)
            return false;

        // Check endpoints still match pin positions (component may have moved)
        var (startX, startY) = connection.StartPin.GetAbsolutePosition();
        var (endX, endY) = connection.EndPin.GetAbsolutePosition();
        var firstSeg = connection.RoutedPath.Segments[0];
        var lastSeg = connection.RoutedPath.Segments[^1];

        double startDist = Distance(firstSeg.StartPoint.X, firstSeg.StartPoint.Y, startX, startY);
        double endDist = Distance(lastSeg.EndPoint.X, lastSeg.EndPoint.Y, endX, endY);

        if (startDist > EndpointToleranceMicrometers || endDist > EndpointToleranceMicrometers)
            return false;

        // Check path doesn't pass through component obstacles
        return !router.IsPathBlocked(connection.RoutedPath.Segments);
    }

    private static double Distance(double x1, double y1, double x2, double y2)
    {
        double dx = x2 - x1;
        double dy = y2 - y1;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// Tries to route all connections in the given order (full re-route).
    /// </summary>
    private (bool allValid, int failedCount) TryRouteInOrder(
        List<WaveguideConnection> orderedConnections,
        WaveguideRouter router,
        Action? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        // Clear all waveguide obstacles from previous routing
        router.PathfindingGrid!.ClearAllWaveguideObstacles();

        int failedCount = 0;
        var routedSoFar = new List<WaveguideConnection>();

        // Route each connection sequentially
        foreach (var connection in orderedConnections)
        {
            if (cancellationToken.IsCancellationRequested)
                return (false, failedCount);

            connection.RecalculateTransmission(_router, cancellationToken: cancellationToken);
            RefreshStyledObstacleCollision(connection, router);
            progressCallback?.Invoke();

            // Register ANY path with valid geometry as an obstacle, including blocked fallbacks.
            // This ensures:
            // 1. A* routes see Manhattan fallback paths as obstacles
            // 2. Manhattan fallback paths see other Manhattan paths
            // Paths without valid geometry (empty or disconnected) are not registered.
            if (connection.IsPathValid && connection.RoutedPath != null)
            {
                router.PathfindingGrid.AddWaveguideObstacle(
                    connection.Id,
                    connection.RoutedPath.Segments,
                    WaveguideWidthMicrometers);

                // Count blocked fallbacks and routes that geometrically cross a sibling
                // as routing failures for ordering optimization. Grid obstacles cannot
                // represent sub-cell pin pitches (flat PDK components), so the geometric
                // check decides whether an ordering counts as clean.
                if (connection.IsBlockedFallback || CrossesAnyRoutedSibling(connection, routedSoFar))
                {
                    failedCount++;
                }
                routedSoFar.Add(connection);
            }
            else
            {
                failedCount++;
            }
        }

        return (failedCount == 0, failedCount);
    }

    /// <summary>
    /// True when the connection's routed geometry properly crosses any already-routed
    /// sibling in this pass. Touching endpoints (shared fan-out regions) do not count.
    /// </summary>
    private static bool CrossesAnyRoutedSibling(
        WaveguideConnection connection, List<WaveguideConnection> routedSoFar)
    {
        return routedSoFar.Any(other =>
            other.RoutedPath != null &&
            PathIntersectionDetector.Crosses(connection.RoutedPath!, other.RoutedPath));
    }

    /// <summary>
    /// Generates different orderings to try.
    /// Prioritizes connections by complexity (longer paths first, or paths involving blocked pins).
    /// </summary>
    private List<List<WaveguideConnection>> GenerateOrderings(List<WaveguideConnection> connections, int maxOrderings)
    {
        var orderings = new List<List<WaveguideConnection>>();

        if (connections.Count <= 1)
            return orderings;

        // Strategy 1: Reverse order
        var reversed = connections.ToList();
        reversed.Reverse();
        orderings.Add(reversed);

        // Strategy 2: Sort by estimated path length (longer first - they need more space)
        var byLength = connections.OrderByDescending(c =>
        {
            var (x1, y1) = c.StartPin.GetAbsolutePosition();
            var (x2, y2) = c.EndPin.GetAbsolutePosition();
            return Math.Sqrt(Math.Pow(x2 - x1, 2) + Math.Pow(y2 - y1, 2));
        }).ToList();
        orderings.Add(byLength);

        // Strategy 3: Sort by estimated path length (shorter first - they block less)
        var byLengthAsc = connections.OrderBy(c =>
        {
            var (x1, y1) = c.StartPin.GetAbsolutePosition();
            var (x2, y2) = c.EndPin.GetAbsolutePosition();
            return Math.Sqrt(Math.Pow(x2 - x1, 2) + Math.Pow(y2 - y1, 2));
        }).ToList();
        orderings.Add(byLengthAsc);

        // Strategy 4: Shuffle randomly (simple randomization)
        if (connections.Count >= 3 && orderings.Count < maxOrderings)
        {
            var random = new Random(42); // Fixed seed for reproducibility
            var shuffled = connections.OrderBy(_ => random.Next()).ToList();
            orderings.Add(shuffled);
        }

        // Strategy 5: Another random shuffle
        if (connections.Count >= 3 && orderings.Count < maxOrderings)
        {
            var random = new Random(123);
            var shuffled = connections.OrderBy(_ => random.Next()).ToList();
            orderings.Add(shuffled);
        }

        // Remove duplicates and limit to maxOrderings
        return orderings
            .Where(o => !o.SequenceEqual(connections)) // Remove if same as original
            .Distinct(new ListComparer<WaveguideConnection>())
            .Take(maxOrderings)
            .ToList();
    }

    /// <summary>
    /// Reorders the internal Connections list to match the given order. Runs on the
    /// routing thread; connections added by the UI since the snapshot are re-appended
    /// so a concurrent add is never silently dropped by the reorder.
    /// </summary>
    private void ReorderConnections(List<WaveguideConnection> newOrder)
    {
        lock (_connectionsSync)
        {
            var lateAdditions = Connections.Where(c => !newOrder.Contains(c)).ToList();
            Connections.Clear();
            Connections.AddRange(newOrder);
            Connections.AddRange(lateAdditions);
        }
    }

    /// <summary>
    /// Helper class for comparing lists.
    /// </summary>
    private class ListComparer<T> : IEqualityComparer<List<T>>
    {
        public bool Equals(List<T>? x, List<T>? y)
        {
            if (x == null || y == null) return x == y;
            return x.SequenceEqual(y);
        }

        public int GetHashCode(List<T> obj)
        {
            return obj.Aggregate(0, (hash, item) => hash ^ (item?.GetHashCode() ?? 0));
        }
    }

    /// <summary>
    /// Recalculates transmission for connections involving a specific component.
    /// When UseSequentialRouting is enabled, this triggers a full recalculation
    /// to ensure proper collision avoidance.
    /// </summary>
    public void RecalculateTransmissionsForComponent(Component component)
    {
        var router = _router;

        if (UseSequentialRouting && router.PathfindingGrid != null)
        {
            // With sequential routing, we need to recalculate all connections
            // because moving one component might free up space for better routes
            RecalculateAllTransmissions();
        }
        else
        {
            // Simple routing: only recalculate affected connections.
            // Snapshot: callers may run this off the UI thread — see _connectionsSync.
            foreach (var connection in SnapshotConnections())
            {
                if (connection.StartPin.ParentComponent == component ||
                    connection.EndPin.ParentComponent == component)
                {
                    connection.RecalculateTransmission(_router);
                }
            }
        }
    }

    /// <summary>
    /// Converts waveguide connections to S-Matrix compatible dictionary.
    /// Uses the LogicalPin IDOutFlow/IDInFlow for proper S-Matrix integration.
    /// Connections are bidirectional: light can flow in either direction through a waveguide.
    /// Physical pins without linked logical pins are skipped (they don't participate in light simulation).
    /// </summary>
    public Dictionary<(Guid PinIdInflow, Guid PinIdOutflow), Complex> GetConnectionTransfers()
    {
        // Snapshot under the lock: the crossing pass may swap connections
        // structurally on the routing thread while the S-matrix is being built.
        var transfers = new Dictionary<(Guid, Guid), Complex>();
        // Snapshot: simulation reads this while routing/commands may mutate the list.
        foreach (var conn in SnapshotConnections())
        {
            // Only include connections where both physical pins have linked logical pins
            if (conn.StartPin.LogicalPin == null || conn.EndPin.LogicalPin == null)
            {
                continue;
            }

            // Forward: light flows from StartPin OutFlow to EndPin InFlow
            var startPinOutFlow = conn.StartPin.LogicalPin.IDOutFlow;
            var endPinInFlow = conn.EndPin.LogicalPin.IDInFlow;
            transfers[(startPinOutFlow, endPinInFlow)] = conn.TransmissionCoefficient;

            // Reverse: light flows from EndPin OutFlow to StartPin InFlow
            // Waveguide connections are inherently bidirectional
            var endPinOutFlow = conn.EndPin.LogicalPin.IDOutFlow;
            var startPinInFlow = conn.StartPin.LogicalPin.IDInFlow;
            transfers[(endPinOutFlow, startPinInFlow)] = conn.TransmissionCoefficient;
        }
        return transfers;
    }
}
