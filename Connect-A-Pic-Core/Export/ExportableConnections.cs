using CAP_Core.Components.Connections;

namespace CAP_Core.Export;

/// <summary>
/// Central definition of which routed waveguide connections may be emitted as export
/// geometry. A connection with no computed route, a blocked-fallback route (drawn
/// through an obstacle because no clean path was found), or invalid geometry (e.g. a
/// bend radius violation) must never silently appear in the exported GDS — the design
/// still exports, but that connection's geometry is left out. Both the Nazca and
/// gdsfactory exporters share this single predicate so a connection can never be
/// exportable in one backend and skipped in the other.
/// </summary>
public static class ExportableConnections
{
    /// <summary>
    /// True when a connection's routed path is real, exportable geometry: a route
    /// exists and is neither a blocked fallback nor invalid geometry.
    /// </summary>
    public static bool IsExportable(this WaveguideConnection connection) =>
        connection.RoutedPath != null
        && !connection.RoutedPath.IsBlockedFallback
        && !connection.RoutedPath.IsInvalidGeometry;

    /// <summary>
    /// Filters a connection sequence down to the ones whose geometry may be exported.
    /// </summary>
    public static IEnumerable<WaveguideConnection> WhereExportable(
        this IEnumerable<WaveguideConnection> connections) =>
        connections.Where(IsExportable);

    /// <summary>
    /// The connections skipped because their route is missing, blocked, or invalid, in
    /// the source order — used to build the post-export "N connections omitted" report.
    /// </summary>
    public static IReadOnlyList<WaveguideConnection> CollectSkipped(
        this IEnumerable<WaveguideConnection> connections) =>
        connections.Where(c => !c.IsExportable()).ToList();
}
