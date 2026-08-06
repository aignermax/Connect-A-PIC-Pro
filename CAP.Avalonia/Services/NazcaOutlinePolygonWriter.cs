using System.Globalization;
using System.Text;
using CAP.Avalonia.Controls.Rendering;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Core;
using CAP_Core.Export;

namespace CAP.Avalonia.Services;

/// <summary>
/// Emits a group's outline polygons — the render-only background geometry of a GDS
/// import (base plates, exclusion zones, logos: the top cell's own non-routing
/// polygons, carried on <see cref="Component.OutlinePolygons"/>) — as
/// <c>nd.Polygon(points=…, layer=(L, D))</c> calls on their ORIGINAL layer/datatype,
/// so imported non-routing geometry round-trips through the Whole-Layout export
/// instead of vanishing (manufacturing needs the original layers). Positions use the
/// same world-space mapping the canvas renderer applies
/// (<see cref="ComponentOutlineRenderer.TransformOutlinePoint"/>), negated into
/// Nazca's Y-up frame like every other coordinate in the script.
/// </summary>
public static class NazcaOutlinePolygonWriter
{
    /// <summary>
    /// Appends every canvas group's outline polygons (recursively, nested groups
    /// included). In a mixed-backend partial export a group whose children are ALL
    /// excluded by <paramref name="include"/> contributes nothing.
    /// </summary>
    /// <param name="sb">Target script builder, positioned inside <c>create_design()</c>.</param>
    /// <param name="canvas">The design canvas being exported.</param>
    /// <param name="include">Optional predicate selecting exported components (partial export).</param>
    public static void AppendGroupOutlinePolygons(
        StringBuilder sb, DesignCanvasViewModel canvas, Func<Component, bool>? include = null)
    {
        foreach (var compVm in canvas.Components)
        {
            if (compVm.Component is ComponentGroup group)
                AppendGroup(sb, group, include);
        }
    }

    private static void AppendGroup(StringBuilder sb, ComponentGroup group, Func<Component, bool>? include)
    {
        if (group.OutlinePolygons is { Count: > 0 } outlines
            && (include is null || group.GetAllComponentsRecursive().Any(include)))
        {
            var ci = CultureInfo.InvariantCulture;
            var groupName = SimpleNazcaExporter.SanitizePythonComment(group.GroupName);
            sb.AppendLine($"        # Group '{groupName}' outline geometry (original GDS layers)");
            foreach (var polygon in outlines)
            {
                var points = WorldPointsOpen(group, polygon);
                if (points.Count < 3)
                    continue; // a degenerate ring (point/line) is not a polygon
                var layer = $"({polygon.Layer.ToString(ci)}, {polygon.DataType.ToString(ci)})";
                sb.AppendLine($"        nd.Polygon(points=[{string.Join(",", points)}], layer={layer}).put(0, 0)");
            }
        }

        foreach (var child in group.ChildComponents)
        {
            if (child is ComponentGroup nested)
                AppendGroup(sb, nested, include);
        }
    }

    /// <summary>
    /// The polygon's vertices as Nazca-formatted world coordinates, WITHOUT the GDS
    /// closing repeat: <c>nd.Polygon</c> closes the ring itself, so a repeated vertex
    /// would only add a degenerate zero-length edge.
    /// </summary>
    private static List<string> WorldPointsOpen(ComponentGroup group, OutlinePolygon polygon)
    {
        var ci = CultureInfo.InvariantCulture;
        var points = new List<string>(polygon.Points.Count);
        foreach (var point in polygon.Points)
        {
            var world = ComponentOutlineRenderer.TransformOutlinePoint(
                point, group.PhysicalX, group.PhysicalY,
                group.WidthMicrometers, group.HeightMicrometers, group.RotationDegrees);
            var (nx, ny) = NazcaCoordinateMapper.ToNazca(world.X, world.Y);
            var formatted =
                $"({NazcaCoordinateMapper.NormalizeZero(nx).ToString("F2", ci)},{NazcaCoordinateMapper.NormalizeZero(ny).ToString("F2", ci)})";
            if (points.Count == 0 || points[^1] != formatted)
                points.Add(formatted);
        }
        if (points.Count > 1 && points[0] == points[^1])
            points.RemoveAt(points.Count - 1);
        return points;
    }
}
