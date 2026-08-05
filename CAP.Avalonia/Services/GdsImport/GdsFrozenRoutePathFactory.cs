using CAP_Core.Components.Core;
using CAP_Core.Routing;
using CAP_DataAccess.Import.Gds;

namespace CAP.Avalonia.Services.GdsImport;

/// <summary>
/// Converts an imported top-cell waveguide polygon (<see cref="GdsOutlinePolygon"/>,
/// app-space Y-down outline) into a pin-less <see cref="FrozenWaveguidePath"/> for
/// the created import group. The frozen-path model holds centerline segments
/// (straight/bend) only — there is no polygon-body representation, and centerline
/// extraction from outlines is issue #814 — so the honest v1 geometry is the
/// polygon OUTLINE traced as a closed ring of straight segments: the routing
/// silhouette becomes visible (and moves/persists with the group) without
/// pretending to be a re-routable connection.
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

    private static StraightSegment CreateSegment(GdsOutlinePoint start, GdsOutlinePoint end, double offsetXUm, double offsetYUm)
    {
        double angleDegrees = Math.Atan2(end.Y - start.Y, end.X - start.X) * 180.0 / Math.PI;
        return new StraightSegment(start.X + offsetXUm, start.Y + offsetYUm, end.X + offsetXUm, end.Y + offsetYUm, angleDegrees);
    }
}
