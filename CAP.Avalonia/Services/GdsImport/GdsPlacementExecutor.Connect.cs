using System.Globalization;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Analysis;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Routing;

namespace CAP.Avalonia.Services.GdsImport;

/// <summary>
/// The connection stage of <see cref="GdsPlacementExecutor"/>, split out to keep
/// each executor file under the project's 500-line gate.
/// <para>
/// Connections whose geometry the import already recovered are attached as
/// FROZEN cached routes (<see cref="DesignCanvasViewModel.ConnectPinsWithCachedRoute"/>
/// — the same mechanism .lun loading uses for its cached routes) and never see
/// the router: route-derived connections trace their drawn top-cell polygons
/// (<see cref="GdsFrozenRoutePathFactory.CreateConnectionRoute"/>), and
/// coincident-pin abutments get the exact pin-to-pin straight (opposing
/// coincident pins need no waveguide — the router's degenerate CSC fallback
/// only flagged them blocked). The single deferred recalculation at the end
/// therefore runs only for connections that still need a route (auto-connect
/// pairs, route-derived pairs without traceable geometry) — for a route-derived
/// import the re-route storm disappears entirely.
/// </para>
/// </summary>
public sealed partial class GdsPlacementExecutor
{
    /// <summary>
    /// Recreates the plan's connections; returns the connections actually
    /// created so the validation stage can check exactly this execution's additions.
    /// Connections with recovered geometry are added frozen with their cached
    /// route, the rest deferred (<see cref="DesignCanvasViewModel.ConnectPins"/>)
    /// and routed in ONE recalculation at the end (the same pattern the
    /// auto-connect pass uses) instead of a per-connection re-route storm — an
    /// <c>await ConnectPinsAsync</c> per connection costs a full re-route each
    /// (O(N²)). The recalculation (when needed at all) runs BEFORE this method
    /// returns so the validation stage keeps seeing fully routed connections.
    /// </summary>
    private async Task<List<WaveguideConnection>> ConnectAllAsync(
        GdsPlacementPlan plan,
        IReadOnlyList<ComponentViewModel?> placedViewModels,
        GdsPlacementReport report,
        IProgress<string>? progress,
        CancellationToken ct,
        (double X, double Y) originOffset)
    {
        var created = new List<WaveguideConnection>();
        var awaitingRoute = 0;
        var frozenCached = 0;
        var stageProgress = StageProgress(progress, "Connecting pins");
        for (var i = 0; i < plan.Connections.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (i % UiYieldInterval == UiYieldInterval - 1)
                await Task.Yield();
            var connection = plan.Connections[i];
            stageProgress?.Report(i + 1, plan.Connections.Count);

            if (connection.InvolvesTopLevelPort)
            {
                report.SkippedConnections.Add(
                    $"{Describe(connection)}: {connection.Note ?? "involves a top-cell port."}");
                continue;
            }

            var startVm = placedViewModels[connection.A.InstanceIndex];
            var endVm = placedViewModels[connection.B.InstanceIndex];
            if (startVm is null || endVm is null)
            {
                report.SkippedConnections.Add(
                    $"{Describe(connection)}: an endpoint instance was not placed.");
                continue;
            }

            var startPin = startVm.Component.PhysicalPins.FirstOrDefault(p => p.Name == connection.A.PinName);
            var endPin = endVm.Component.PhysicalPins.FirstOrDefault(p => p.Name == connection.B.PinName);
            if (startPin is null || endPin is null)
            {
                report.SkippedConnections.Add(
                    $"{Describe(connection)}: pin not found on the placed component.");
                continue;
            }

            WaveguideConnectionViewModel? connectionVm;
            var cachedRoute = TryBuildCachedRoute(connection, startPin, endPin, originOffset);
            if (cachedRoute is not null)
            {
                connectionVm = _canvas.ConnectPinsWithCachedRoute(startPin, endPin, cachedRoute);
                if (connectionVm is not null)
                {
                    // Hardcoded like a .lun-loaded cached route: frozen, so no
                    // later routing pass replaces the imported geometry (an
                    // endpoint move unfreezes and re-routes, as for any frozen route).
                    connectionVm.Connection.IsRouteFrozen = true;
                    frozenCached++;
                }
            }
            else
            {
                connectionVm = _canvas.ConnectPins(startPin, endPin);
                if (connectionVm is not null)
                    awaitingRoute++;
            }

            if (connectionVm is not null)
            {
                created.Add(connectionVm.Connection);
                report.ConnectedCount++;
                if (connection.IsRouteDerived)
                    report.RouteDerivedCount++;
            }
        }

        report.CachedRouteCount = frozenCached;
        if (frozenCached > 0)
        {
            // ConnectPinsWithCachedRoute does not invalidate per call (unlike
            // ConnectPins); one invalidation covers the whole cached batch.
            _canvas.InvalidateSimulation();
        }
        if (awaitingRoute > 0)
            await _canvas.RecalculateRoutesAsync(); // one routing pass for the connections without recovered geometry
        return created;
    }

    /// <summary>
    /// The recovered route for a connection whose geometry the import already
    /// knows, or null when the batch routing pass must route it: route-derived
    /// connections trace the polygons they were derived from (anchored at the
    /// placed pins), coincident-pin abutments get the exact pin-to-pin straight.
    /// </summary>
    private static RoutedPath? TryBuildCachedRoute(
        GdsConnectionInstruction connection,
        PhysicalPin startPin,
        PhysicalPin endPin,
        (double X, double Y) originOffset)
    {
        var (startX, startY) = startPin.GetAbsolutePosition();
        var (endX, endY) = endPin.GetAbsolutePosition();

        if (connection.IsRouteDerived)
        {
            return connection.SourcePolygons.Count == 0
                ? null // a hand-built plan may carry no geometry — route it instead.
                : GdsFrozenRoutePathFactory.CreateConnectionRoute(
                    connection.SourcePolygons, (startX, startY), (endX, endY), originOffset.X, originOffset.Y);
        }

        // Abutment: the matcher only pairs pins that coincide (within its
        // tolerance) and oppose — the pin-to-pin straight IS the honest route.
        // Anything longer was not a drawn abutment and keeps the router.
        double dx = endX - startX;
        double dy = endY - startY;
        if ((dx * dx) + (dy * dy) > DegenerateRouteThresholdUm * DegenerateRouteThresholdUm)
            return null;

        var path = new RoutedPath();
        if (dx != 0 || dy != 0)
        {
            path.Segments.Add(new StraightSegment(
                startX, startY, endX, endY, Math.Atan2(dy, dx) * 180.0 / Math.PI));
        }
        else
        {
            // Exactly coincident pins: a degenerate zero-length straight — the
            // same shape the router's own placeholder fallback emits for this
            // case, just without the false blocked flag.
            path.Segments.Add(new StraightSegment(startX, startY, endX, endY, 0.0));
        }
        return path;
    }

    /// <summary>
    /// Post-batch honesty net: runs <see cref="DesignValidator"/> over exactly the
    /// connections created by this execution (abutment + auto-connect), including
    /// overlap checks against the frozen paths of groups already on the canvas,
    /// and appends the issues to the report as validation warnings.
    /// Coincident-pin connections (a perfect GDS abutment: the pins sit at the
    /// same point, below the router's own 1 µm endpoint tolerance) have NO routed
    /// geometry to validate — the CSC fallback still flags their degenerate route
    /// as blocked, so including them would plaster every standard abutment import
    /// with false BlockedPath warnings. They are filtered out here instead.
    /// </summary>
    private void ValidateCreatedConnections(
        IReadOnlyList<WaveguideConnection> createdConnections,
        GdsPlacementReport report)
    {
        var routable = createdConnections
            .Where(c => PinDistanceUm(c) >= DegenerateRouteThresholdUm)
            .ToList();
        if (routable.Count == 0)
            return;

        var existingGroups = _canvas.Components
            .Select(c => c.Component)
            .OfType<ComponentGroup>()
            .ToList();
        var issues = new DesignValidator().Validate(routable, existingGroups);
        foreach (var issue in issues)
        {
            report.ValidationWarnings.Add(string.Create(CultureInfo.InvariantCulture,
                $"{issue.Type} at ({issue.X:0.#}, {issue.Y:0.#}) µm — {issue.Description}"));
        }
    }

    /// <summary>
    /// Pin-to-pin distance (µm) below which a route is degenerate — aligned with
    /// the router's endpoint tolerance (a route this short is a perfect abutment,
    /// not a waveguide).
    /// </summary>
    private const double DegenerateRouteThresholdUm = 1.0;

    private static double PinDistanceUm(WaveguideConnection connection)
    {
        var (x1, y1) = connection.StartPin.GetAbsolutePosition();
        var (x2, y2) = connection.EndPin.GetAbsolutePosition();
        var dx = x2 - x1;
        var dy = y2 - y1;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
