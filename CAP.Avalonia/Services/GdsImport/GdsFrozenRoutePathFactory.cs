using CAP_Core.Components.Core;
using CAP_Core.Routing;
using CAP_DataAccess.Import.Gds;

namespace CAP.Avalonia.Services.GdsImport;

/// <summary>
/// Converts imported top-cell route polygons (<see cref="GdsOutlinePolygon"/>,
/// app-space Y-down outlines) into canvas route geometry. The frozen-path model
/// holds centerline segments (straight/bend) only — there is no polygon-body
/// representation, and centerline extraction from outlines is issue #814 — so
/// the honest v1 geometry is the polygon OUTLINE traced as a ring of straight
/// segments: the routing silhouette becomes visible (and moves/persists with
/// the group) without pretending to be a re-routable centerline.
/// </summary>
public static class GdsFrozenRoutePathFactory
{
    /// <summary>
    /// Traces <paramref name="polygon"/>'s outline as one straight segment per
    /// edge, closing the ring defensively when the source did not repeat the
    /// first point at the end (GDS BOUNDARY polygons are closed by convention).
    /// The result has no pins: imported route geometry renders, moves with the
    /// group and round-trips the .lun file, but group edit mode, ungroup and
    /// simulation skip it.
    /// </summary>
    /// <param name="polygon">Top-cell waveguide polygon, in plan space.</param>
    /// <param name="offsetXUm">
    /// Uniform X translation (µm) from plan space to canvas space — the import
    /// origin offset the placements received (0 when the canvas was empty).
    /// Frozen paths hold absolute canvas coordinates, so they need the same shift.
    /// </param>
    /// <param name="offsetYUm">Uniform Y translation (µm), see <paramref name="offsetXUm"/>.</param>
    public static FrozenWaveguidePath Create(GdsOutlinePolygon polygon, double offsetXUm = 0.0, double offsetYUm = 0.0)
    {
        ArgumentNullException.ThrowIfNull(polygon);
        var path = new RoutedPath();
        for (var i = 0; i + 1 < polygon.Points.Count; i++)
            path.Segments.Add(CreateSegment(polygon.Points[i], polygon.Points[i + 1], offsetXUm, offsetYUm));

        var points = polygon.Points;
        if (points.Count > 1)
        {
            var first = points[0];
            var last = points[^1];
            if (first.X != last.X || first.Y != last.Y)
                path.Segments.Add(CreateSegment(last, first, offsetXUm, offsetYUm));
        }

        return new FrozenWaveguidePath
        {
            Path = path,
            StartPin = null,
            EndPin = null,
        };
    }

    /// <summary>
    /// Builds the cached route for a route-derived connection from the polygons
    /// it was derived from: the drawn GDS route IS the intended geometry, so the
    /// connection loads with this hardcoded path (frozen) instead of being
    /// re-routed by A* — the same mechanism .lun loading uses for cached routes
    /// (<see cref="WaveguideConnection.RestoreCachedPath"/>).
    /// <para>
    /// Geometry: the polygons' outlines traced as straight segments, anchored at
    /// the two pins. Each polygon ring is entered at the vertex nearest the
    /// running position (nearest-neighbor over the not-yet-traced rings, starting
    /// at <paramref name="startUm"/>), walked in full, and the path closes to
    /// <paramref name="endUm"/> — so the first segment starts exactly at the
    /// start pin and the last ends exactly at the end pin, which is what the
    /// frozen-route keep-checks (<see cref="WaveguideConnection.FrozenPathStillMatchesPins"/>)
    /// require. Degenerate (zero-length) edges are dropped; a path that would
    /// come out empty (no polygon with a real outline) returns null so the
    /// caller can fall back to routing.
    /// </para>
    /// </summary>
    /// <param name="polygons">The network's route polygons, in plan space.</param>
    /// <param name="startUm">Start pin's absolute canvas position (µm).</param>
    /// <param name="endUm">End pin's absolute canvas position (µm).</param>
    /// <param name="offsetXUm">
    /// Uniform X translation (µm) from plan space to canvas space — the import
    /// origin offset the placements received (0 when the canvas was empty).
    /// </param>
    /// <param name="offsetYUm">Uniform Y translation (µm), see <paramref name="offsetXUm"/>.</param>
    /// <returns>The traced route, or null when the polygons hold no traceable outline.</returns>
    public static RoutedPath? CreateConnectionRoute(
        IReadOnlyList<GdsOutlinePolygon> polygons,
        (double X, double Y) startUm,
        (double X, double Y) endUm,
        double offsetXUm = 0.0,
        double offsetYUm = 0.0)
    {
        ArgumentNullException.ThrowIfNull(polygons);
        var rings = new List<IReadOnlyList<GdsOutlinePoint>>();
        foreach (var polygon in polygons)
        {
            var ring = DistinctRingPoints(polygon.Points);
            if (ring.Count >= 2)
                rings.Add(ring);
        }
        if (rings.Count == 0)
            return null;

        var path = new RoutedPath();
        var current = startUm;
        while (rings.Count > 0)
        {
            var (ringIndex, entryVertex) = NearestRingEntry(rings, current, offsetXUm, offsetYUm);
            var ring = rings[ringIndex];
            rings.RemoveAt(ringIndex);

            var entry = ToCanvas(ring[entryVertex]);
            AppendStraight(path, current, entry);
            for (var step = 1; step <= ring.Count; step++)
            {
                AppendStraight(path,
                    ToCanvas(ring[(entryVertex + step - 1) % ring.Count]),
                    ToCanvas(ring[(entryVertex + step) % ring.Count]));
            }
            current = entry;
        }
        AppendStraight(path, current, endUm);

        return path.Segments.Count > 0 ? path : null;

        (double X, double Y) ToCanvas(GdsOutlinePoint point) =>
            (point.X + offsetXUm, point.Y + offsetYUm);
    }

    /// <summary>
    /// The polygon's vertices without the GDS closing repeat and without
    /// consecutive duplicates — the ring walk and the degenerate-segment guard
    /// both rely on adjacent vertices being distinct.
    /// </summary>
    private static IReadOnlyList<GdsOutlinePoint> DistinctRingPoints(IReadOnlyList<GdsOutlinePoint> points)
    {
        var ring = new List<GdsOutlinePoint>(points.Count);
        foreach (var point in points)
        {
            if (ring.Count == 0 || ring[^1] != point)
                ring.Add(point);
        }
        if (ring.Count > 1 && ring[0] == ring[^1])
            ring.RemoveAt(ring.Count - 1);
        return ring;
    }

    /// <summary>
    /// Index of the ring (and its vertex) nearest to <paramref name="fromUm"/> —
    /// the hop target of the nearest-neighbor chain.
    /// </summary>
    private static (int RingIndex, int VertexIndex) NearestRingEntry(
        List<IReadOnlyList<GdsOutlinePoint>> rings, (double X, double Y) fromUm,
        double offsetXUm, double offsetYUm)
    {
        var bestRing = 0;
        var bestVertex = 0;
        var bestDistanceSquared = double.MaxValue;
        for (var r = 0; r < rings.Count; r++)
        {
            for (var v = 0; v < rings[r].Count; v++)
            {
                double dx = rings[r][v].X + offsetXUm - fromUm.X;
                double dy = rings[r][v].Y + offsetYUm - fromUm.Y;
                double distanceSquared = (dx * dx) + (dy * dy);
                if (distanceSquared < bestDistanceSquared)
                {
                    bestDistanceSquared = distanceSquared;
                    bestRing = r;
                    bestVertex = v;
                }
            }
        }
        return (bestRing, bestVertex);
    }

    /// <summary>Appends a straight segment, skipping degenerate (zero-length) hops.</summary>
    private static void AppendStraight(RoutedPath path, (double X, double Y) start, (double X, double Y) end)
    {
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        if ((dx * dx) + (dy * dy) < 1e-18)
            return;
        path.Segments.Add(new StraightSegment(
            start.X, start.Y, end.X, end.Y, Math.Atan2(dy, dx) * 180.0 / Math.PI));
    }

    private static StraightSegment CreateSegment(GdsOutlinePoint start, GdsOutlinePoint end, double offsetXUm, double offsetYUm)
    {
        double angleDegrees = Math.Atan2(end.Y - start.Y, end.X - start.X) * 180.0 / Math.PI;
        return new StraightSegment(start.X + offsetXUm, start.Y + offsetYUm, end.X + offsetXUm, end.Y + offsetYUm, angleDegrees);
    }
}
