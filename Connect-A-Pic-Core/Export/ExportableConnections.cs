using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Routing;

namespace CAP_Core.Export;

/// <summary>
/// Central definition of which routed geometry may be emitted as export geometry. Only
/// truly degenerate geometry is excluded: a placeholder the router substituted for a
/// self-crossing fallback with no optical model (<see cref="RoutedPath.IsPlaceholderGeometry"/>),
/// or geometry that violates physical constraints (<see cref="RoutedPath.IsInvalidGeometry"/>).
/// A missing route (<c>null</c>) is NOT excluded — that is the long-standing routeless state
/// (e.g. a legacy design file loaded without cached segments, never re-routed until something
/// moves) and both exporters already draw it as a direct pin-to-pin fallback; excluding it here
/// would silently blank out that geometry instead. <see cref="RoutedPath.IsBlockedFallback"/> is
/// deliberately NOT part of this predicate: besides the placeholder case it also covers a
/// fallback that merely grazes an obstacle (real, exportable geometry) and the crossing
/// diagnostic <c>WaveguideConnectionManager</c> stamps on an unresolved sibling overlap —
/// including a metal/optical crossing legitimately resolved by a bridge marker — so keying
/// export exclusion off it would silently drop connections that are meant to render. Both the
/// Nazca and gdsfactory exporters share this single predicate so a connection can never be
/// exportable in one backend and skipped in the other.
/// </summary>
public static class ExportableConnections
{
    /// <summary>
    /// True when a routed path may be emitted as export geometry: no route at all (falls back
    /// to the pin-to-pin straight), or a route that is neither a placeholder nor invalid.
    /// </summary>
    public static bool IsExportable(this RoutedPath? routedPath) =>
        routedPath == null || (!routedPath.IsPlaceholderGeometry && !routedPath.IsInvalidGeometry);

    /// <summary>True when a connection's routed path may be emitted as export geometry.</summary>
    public static bool IsExportable(this WaveguideConnection connection) =>
        connection.RoutedPath.IsExportable();

    /// <summary>
    /// The single skip-and-describe check shared by both exporters' live-connection and
    /// frozen-group-path loops: when <paramref name="routedPath"/> is not exportable, appends
    /// its endpoint description to <paramref name="skippedConnections"/> (when the caller
    /// collects one) and returns true so the caller can <c>continue</c> past it. One shared
    /// method means the skip condition and its report can never drift apart across the four
    /// call sites (Nazca/gdsfactory × live/frozen).
    /// </summary>
    public static bool TryRecordSkip(
        RoutedPath? routedPath, PhysicalPin? startPin, PhysicalPin? endPin,
        List<string>? skippedConnections)
    {
        if (routedPath.IsExportable())
            return false;
        skippedConnections?.Add(Describe(startPin, endPin));
        return true;
    }

    /// <summary>
    /// Formats a skipped connection/frozen-path endpoint pair as "Start.Pin → End.Pin" for the
    /// post-export report. Falls back to "?" for a missing pin or parent component instead of
    /// throwing, so a malformed connection never crashes the export flow over a warning message.
    /// </summary>
    public static string Describe(PhysicalPin? startPin, PhysicalPin? endPin) =>
        $"{DescribeEndpoint(startPin)} → {DescribeEndpoint(endPin)}";

    private static string DescribeEndpoint(PhysicalPin? pin) =>
        pin == null ? "?" : $"{pin.ParentComponent?.Identifier ?? "?"}.{pin.Name}";
}
