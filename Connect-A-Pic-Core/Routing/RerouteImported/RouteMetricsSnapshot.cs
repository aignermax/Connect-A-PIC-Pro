using CAP_Core.Components.Connections;

namespace CAP_Core.Routing.RerouteImported;

/// <summary>
/// Aggregate length/bend metrics of a set of routes, captured before and after a
/// re-route pass so the UI can show the user what the re-route changed
/// (issue #857: show the before/after delta, never replace silently).
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
}
