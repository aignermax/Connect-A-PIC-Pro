using CAP_Core.Components.Core;
using CAP_Core.Routing;
using CAP_Core.Routing.InterconnectRouting.SegmentShift;

namespace CAP_Core.Components.Connections;

/// <summary>
/// Post-routing pass that pulls the pin-side straight leads of freshly auto-routed connections
/// onto their pins (<see cref="PinStraightCollapser"/>). The grid escape and cell quantization
/// leave a short forced straight between a pin and the first/last bend; without this pass the
/// user has to shift it away by hand after every re-route. It runs once, after every route is
/// final and registered, so each collapse is validated against ALL sibling routes and component
/// bodies at their final positions — the reason it lives here and not inside a single-route
/// <see cref="WaveguideRouter"/> attempt, which cannot see routes computed later.
/// </summary>
public partial class WaveguideConnectionManager
{
    /// <summary>Rectangle shrink (µm) so a bend that begins on a pin (component edge) is not
    /// counted as entering the component body.</summary>
    private const double ComponentBodyToleranceMicrometers = 0.3;

    /// <summary>A collapse may not bring a route closer to any sibling than it already was, minus
    /// this numeric slack (µm), so it can never introduce or tighten a crossing.</summary>
    private const double SiblingClearanceToleranceMicrometers = 1e-3;

    /// <summary>
    /// Collapses the pin leads of every auto-routed, non-frozen, cleanly routed connection.
    /// Frozen or manually edited routes and blocked fallbacks are left untouched.
    /// </summary>
    private void CollapseAutoRoutePinLeads()
    {
        var connections = SnapshotConnections();
        var componentBodies = CollectComponentBodies(connections);

        foreach (var connection in connections)
        {
            if (!IsPinLeadCollapsible(connection))
                continue;

            var siblings = connections
                .Where(other => other != connection && other.RoutedPath != null && other.IsPathValid)
                .Select(other => other.RoutedPath!)
                .ToList();
            var clearanceBefore = siblings
                .Select(sibling => PathIntersectionDetector.MinimumDistance(connection.RoutedPath!, sibling))
                .ToList();

            PinStraightCollapser.Collapse(connection.RoutedPath!,
                trial => IsCollapseAcceptable(trial, componentBodies, siblings, clearanceBefore));

            _router.PathfindingGrid?.AddWaveguideObstacle(
                connection.Id, connection.RoutedPath!.Segments, WaveguideWidthMicrometers);
            connection.UpdateLossFromPath();
        }
    }

    /// <summary>True for a route the collapse pass may edit: an auto route with a clean,
    /// unfrozen, non-fallback path.</summary>
    private static bool IsPinLeadCollapsible(WaveguideConnection connection) =>
        connection.Type == WaveguideType.Auto
        && !connection.IsRouteFrozen
        && connection.RoutedPath is { IsBlockedFallback: false } path
        && path.IsValid
        && path.Segments.Count > 0;

    /// <summary>
    /// Accepts a collapse trial only when it stays simple, keeps clear of every component body,
    /// and comes no closer to any sibling route than the pre-collapse geometry did.
    /// </summary>
    private static bool IsCollapseAcceptable(RoutedPath trial,
        IReadOnlyList<(double MinX, double MinY, double MaxX, double MaxY)> componentBodies,
        IReadOnlyList<RoutedPath> siblings, IReadOnlyList<double> clearanceBefore)
    {
        if (PathIntersectionDetector.HasSelfIntersection(trial))
            return false;

        foreach (var body in componentBodies)
        {
            if (PathIntersectionDetector.IntersectsRectangle(trial, body.MinX, body.MinY, body.MaxX, body.MaxY))
                return false;
        }

        for (int i = 0; i < siblings.Count; i++)
        {
            if (PathIntersectionDetector.MinimumDistance(trial, siblings[i])
                < clearanceBefore[i] - SiblingClearanceToleranceMicrometers)
                return false;
        }

        return true;
    }

    /// <summary>Bounding rectangles of every component touched by a connection, shrunk by
    /// <see cref="ComponentBodyToleranceMicrometers"/> so edge-mounted pins do not count as inside.</summary>
    private static List<(double MinX, double MinY, double MaxX, double MaxY)> CollectComponentBodies(
        IReadOnlyList<WaveguideConnection> connections)
    {
        var components = new HashSet<Component>();
        foreach (var connection in connections)
        {
            if (connection.StartPin?.ParentComponent is { } start)
                components.Add(start);
            if (connection.EndPin?.ParentComponent is { } end)
                components.Add(end);
        }

        return components
            .Select(c => (
                c.PhysicalX + ComponentBodyToleranceMicrometers,
                c.PhysicalY + ComponentBodyToleranceMicrometers,
                c.PhysicalX + c.WidthMicrometers - ComponentBodyToleranceMicrometers,
                c.PhysicalY + c.HeightMicrometers - ComponentBodyToleranceMicrometers))
            .ToList();
    }
}
