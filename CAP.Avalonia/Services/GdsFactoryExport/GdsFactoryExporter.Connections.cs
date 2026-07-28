using System.Globalization;
using System.Text;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Core;
using CAP_Core.Components.PinKinds;
using CAP_Core.Export;
using CAP_Core.Routing;
using CAP_Core.Routing.MetalRouting;

namespace CAP.Avalonia.Services.GdsFactoryExport;

/// <summary>
/// The connection-geometry half of <see cref="GdsFactoryExporter"/>: waveguide/metal
/// connection emission, unresolved-crossing collection, bridge markers and frozen group
/// paths (split out to keep the exporter below the file-size limit).
/// </summary>
public partial class GdsFactoryExporter
{
    private static void AppendConnections(
        StringBuilder sb, DesignCanvasViewModel canvas, string waveguideKwarg, MetalRoutingSpec metalSpec,
        List<string>? skippedConnections = null, List<string>? unresolvedCrossings = null)
    {
        sb.AppendLine("# Waveguide connections");
        var metalStyle = metalSpec.ToTraceStyle();
        var opticalPaths = new List<IReadOnlyList<PathSegment>>();
        var electrical = new List<CAP_Core.Components.Connections.WaveguideConnection>();
        var unresolvedCrossingCandidates = new List<CAP_Core.Components.Connections.WaveguideConnection>();

        foreach (var connVm in canvas.Connections)
        {
            var conn = connVm.Connection;
            if (conn.StartPin?.ParentComponent?.IsAnalysisTool == true) continue;
            if (conn.EndPin?.ParentComponent?.IsAnalysisTool == true) continue;

            // A placeholder (self-crossing fallback with no optical model) or invalid
            // (bend radius violation) route must never render as geometry — the design
            // still exports, just without this connection's geometry. A missing route is
            // NOT skipped: it falls back to the pin-to-pin straight below, same as before.
            if (ExportableConnections.TryRecordSkip(conn.RoutedPath, conn.StartPin, conn.EndPin, skippedConnections))
                continue;

            // Electrical connections are metal traces, not optical waveguides — draw them as a
            // polygon on the metal layer instead of a routed waveguide cell (issue #682). A
            // connection is metal only when BOTH pins are electrical; a mixed or all-optical
            // connection stays a waveguide (issue #686 review). Metal connections are
            // remembered so bridge markers can be placed where they cross optical paths.
            var metal = IsMetalConnection(conn.StartPin, conn.EndPin) ? metalStyle : null;
            if (metal != null)
                electrical.Add(conn);
            else if (conn.IsBlockedFallback)
                // Real (non-placeholder) geometry that WaveguideConnectionManager's sibling-
                // crossing pass still flagged — it renders (below), but the layout deserves a
                // second look unless a bridge marker actually resolves the crossing.
                unresolvedCrossingCandidates.Add(conn);

            var segments = conn.GetPathSegments();
            if (segments.Count > 0)
            {
                GdsFactorySegmentWriter.AppendSegments(sb, segments, conn.StartPin, conn.EndPin, waveguideKwarg, metal);
                if (metal == null)
                    opticalPaths.Add(segments);
            }
            else if (conn.StartPin != null && conn.EndPin != null)
            {
                GdsFactorySegmentWriter.AppendPinToPinFallback(sb, conn.StartPin, conn.EndPin, waveguideKwarg, metal);
            }
        }

        foreach (var compVm in canvas.Components)
        {
            if (compVm.Component is ComponentGroup group)
                AppendGroupFrozenPaths(sb, group, waveguideKwarg, metalStyle, opticalPaths, skippedConnections);
        }

        AppendBridgeMarkers(sb, electrical, opticalPaths, metalSpec);
        CollectUnresolvedCrossings(unresolvedCrossingCandidates, electrical, metalSpec, unresolvedCrossings);
        sb.AppendLine();
    }

    /// <summary>
    /// Reports the flagged connections a bridge marker does NOT resolve: a crossing is
    /// bridge-resolved only when it is a metal↔optical pair under
    /// <see cref="ElectricalCrossingPolicy.BridgeRequired"/> — exactly the condition
    /// <see cref="AppendBridgeMarkers"/> uses to decide whether to draw a marker at all, so a
    /// candidate that crosses no exported metal trace (an optical×optical crossing, or any
    /// crossing under a policy that never draws a marker) is genuinely unresolved.
    /// </summary>
    private static void CollectUnresolvedCrossings(
        IReadOnlyList<CAP_Core.Components.Connections.WaveguideConnection> candidates,
        IReadOnlyList<CAP_Core.Components.Connections.WaveguideConnection> metalConnections,
        MetalRoutingSpec metalSpec,
        List<string>? unresolvedCrossings)
    {
        if (unresolvedCrossings == null || candidates.Count == 0)
            return;

        var bridgesCrossings = metalSpec.CrossingPolicy == ElectricalCrossingPolicy.BridgeRequired;
        foreach (var candidate in candidates)
        {
            bool resolvedByBridge = bridgesCrossings && candidate.RoutedPath != null
                && metalConnections.Any(metalConn =>
                    metalConn.RoutedPath != null
                    && PathIntersectionDetector.Crosses(candidate.RoutedPath, metalConn.RoutedPath));
            if (!resolvedByBridge)
                unresolvedCrossings.Add(ExportableConnections.Describe(candidate.StartPin, candidate.EndPin));
        }
    }

    /// <summary>
    /// Emits bridge markers where electrical metal traces cross optical waveguide
    /// paths, when the active process requires bridges (#682). The trace geometry
    /// itself is emitted inline by the connection loop above.
    /// </summary>
    private static void AppendBridgeMarkers(
        StringBuilder sb,
        IReadOnlyList<CAP_Core.Components.Connections.WaveguideConnection> electrical,
        IReadOnlyList<IReadOnlyList<PathSegment>> opticalPaths,
        MetalRoutingSpec metalSpec)
    {
        if (metalSpec.CrossingPolicy != ElectricalCrossingPolicy.BridgeRequired || electrical.Count == 0)
            return;

        sb.AppendLine();
        sb.AppendLine("# Electrical bridge markers (metal over waveguide)");
        foreach (var conn in electrical)
        {
            var segments = conn.GetPathSegments();
            if (segments.Count == 0)
                continue;

            var crossings = WaveguideCrossingDetector.FindCrossings(segments, opticalPaths);
            GdsFactoryMetalTraceWriter.AppendBridges(sb, crossings, metalSpec);
        }
    }

    /// <summary>
    /// Exports frozen waveguide paths from a ComponentGroup (and nested groups). A frozen path
    /// between two electrical pins is a metal trace, not a waveguide — mirrors the live
    /// connection loop above (issue #686 review). Frozen optical paths are collected as
    /// crossable geometry for bridge detection. A frozen path with placeholder or invalid
    /// geometry is left out just like a live connection — freezing (grouping) a connection
    /// must not bypass the export filter. A frozen path with NO route at all (a connection
    /// frozen before it was ever routed keeps an empty <c>RoutedPath</c>, not null) renders
    /// the same pin-to-pin fallback a routeless live connection gets, instead of silently
    /// vanishing.
    /// </summary>
    private static void AppendGroupFrozenPaths(
        StringBuilder sb, ComponentGroup group, string waveguideKwarg,
        MetalTraceStyle metalStyle, List<IReadOnlyList<PathSegment>> opticalPaths,
        List<string>? skippedConnections = null)
    {
        foreach (var frozenPath in group.InternalPaths)
        {
            if (frozenPath == null) continue;

            var metal = IsMetalConnection(frozenPath.StartPin, frozenPath.EndPin) ? metalStyle : null;
            var segments = frozenPath.Path?.Segments;
            if (segments == null || segments.Count == 0)
            {
                GdsFactorySegmentWriter.AppendPinToPinFallback(
                    sb, frozenPath.StartPin, frozenPath.EndPin, waveguideKwarg, metal);
                continue;
            }

            if (ExportableConnections.TryRecordSkip(
                    frozenPath.Path, frozenPath.StartPin, frozenPath.EndPin, skippedConnections))
                continue;
            GdsFactorySegmentWriter.AppendSegments(
                sb, segments, frozenPath.StartPin, frozenPath.EndPin, waveguideKwarg, metal);
            if (metal == null)
                opticalPaths.Add(segments);
        }

        foreach (var child in group.ChildComponents)
        {
            if (child is ComponentGroup nested)
                AppendGroupFrozenPaths(sb, nested, waveguideKwarg, metalStyle, opticalPaths, skippedConnections);
        }
    }

}
