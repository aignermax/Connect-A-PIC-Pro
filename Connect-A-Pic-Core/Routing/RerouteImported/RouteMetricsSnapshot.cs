using System.Linq;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;

namespace CAP_Core.Routing.RerouteImported;

/// <summary>
/// Aggregate length/bend metrics of a set of routes, captured before and after a
/// re-route pass so the UI can show the user what the re-route changed
/// (show the before/after delta, never replace silently).
/// </summary>
/// <param name="LengthMicrometers">Total routed length in micrometers.</param>
/// <param name="EquivalentBends">Total equivalent 90° bend count.</param>
public readonly record struct RouteMetricsSnapshot(double LengthMicrometers, double EquivalentBends)
{
    /// <summary>Sums length and bend count over <paramref name="connections"/>' current routes.</summary>
    public static RouteMetricsSnapshot Capture(IEnumerable<WaveguideConnection> connections)
    {
        double length = 0;
        double bends = 0;
        foreach (var connection in connections)
        {
            length += connection.PathLengthMicrometers;
            bends += connection.BendCount;
        }
        return new RouteMetricsSnapshot(length, bends);
    }

    /// <summary>
    /// Sums length and bend count over live <paramref name="connections"/> and frozen
    /// <paramref name="groupPaths"/> so group-internal imported routes contribute to the
    /// before/after delta shown in the UI.
    /// </summary>
    public static RouteMetricsSnapshot Capture(
        IEnumerable<WaveguideConnection> connections,
        IEnumerable<FrozenWaveguidePath> groupPaths)
    {
        var snapshot = Capture(connections);
        double length = snapshot.LengthMicrometers;
        double bends = snapshot.EquivalentBends;
        foreach (var path in groupPaths)
        {
            if (path?.Path is not { Segments.Count: > 0 } routedPath)
                continue;
            length += routedPath.TotalLengthMicrometers;
            bends += routedPath.Segments.Count(s => s is BendSegment);
        }
        return new RouteMetricsSnapshot(length, bends);
    }
}
