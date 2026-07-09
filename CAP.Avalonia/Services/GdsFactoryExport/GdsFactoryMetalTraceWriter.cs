using System.Globalization;
using System.Text;
using CAP_Core.Components.Core;
using CAP_Core.Export;
using CAP_Core.Routing;
using CAP_Core.Routing.MetalRouting;

namespace CAP.Avalonia.Services.GdsFactoryExport;

/// <summary>
/// Emits electrical connections as metal traces into the gdsfactory export (issue #682).
/// Straights and arcs are built with <c>gf.path</c> and extruded on the process metal
/// layer; bridge markers are added as polygons where the process requires them.
/// Coordinate contract matches <see cref="GdsFactorySegmentWriter"/> (Y-up, negated angles).
/// </summary>
public static class GdsFactoryMetalTraceWriter
{
    /// <summary>Bridge marker square edge length as a multiple of the metal trace width.</summary>
    private const double BridgeSizeFactor = 2.0;

    /// <summary>Appends the culture-invariant metal constants to the script header.</summary>
    public static void AppendHeaderConstants(StringBuilder sb, MetalRoutingSpec spec)
    {
        var ci = CultureInfo.InvariantCulture;
        sb.AppendLine("# Electrical metal routing (process-derived)");
        sb.AppendLine($"METAL_WIDTH = {spec.TraceWidthMicrometers.ToString("F2", ci)}  # metal trace width in um");
        sb.AppendLine($"METAL_LAYER = ({spec.MetalGdsLayer.ToString(ci)}, {spec.MetalGdsDatatype.ToString(ci)})");
        sb.AppendLine($"BRIDGE_LAYER = ({spec.BridgeGdsLayer.ToString(ci)}, 0)");
        sb.AppendLine();
    }

    /// <summary>Appends a bridge marker polygon at every metal/waveguide crossing point (app coordinates).</summary>
    public static void AppendBridges(
        StringBuilder sb, IReadOnlyList<(double X, double Y)> crossings, MetalRoutingSpec spec)
    {
        var ci = CultureInfo.InvariantCulture;
        double half = spec.TraceWidthMicrometers * BridgeSizeFactor / 2.0;

        foreach (var crossing in crossings)
        {
            var (cx, cy) = NazcaCoordinateMapper.ToNazca(crossing.X, crossing.Y);
            var x0 = NazcaCoordinateMapper.NormalizeZero(cx - half).ToString("F2", ci);
            var x1 = NazcaCoordinateMapper.NormalizeZero(cx + half).ToString("F2", ci);
            var y0 = NazcaCoordinateMapper.NormalizeZero(cy - half).ToString("F2", ci);
            var y1 = NazcaCoordinateMapper.NormalizeZero(cy + half).ToString("F2", ci);
            sb.AppendLine($"# BRIDGE: metal crosses waveguide near ({NazcaCoordinateMapper.NormalizeZero(cx).ToString("F2", ci)}, {NazcaCoordinateMapper.NormalizeZero(cy).ToString("F2", ci)})");
            sb.AppendLine($"c.add_polygon([({x0}, {y0}), ({x1}, {y0}), ({x1}, {y1}), ({x0}, {y1})], layer=BRIDGE_LAYER)");
        }
    }

    private static void AppendStraight(StringBuilder sb, double sx, double sy, double ex, double ey)
    {
        var ci = CultureInfo.InvariantCulture;
        double dx = ex - sx;
        double dy = ey - sy;
        double length = Math.Sqrt(dx * dx + dy * dy);
        double angleDeg = Math.Atan2(dy, dx) * 180.0 / Math.PI;

        sb.AppendLine($"_metal = c.add_ref(gf.path.straight(length={length.ToString("F2", ci)})"
                      + ".extrude(width=METAL_WIDTH, layer=METAL_LAYER))");
        AppendPlacement(sb, angleDeg, sx, sy, ci);
    }

    private static void AppendBend(StringBuilder sb, BendSegment bend, double sx, double sy)
    {
        var ci = CultureInfo.InvariantCulture;
        var radius = bend.RadiusMicrometers.ToString("F2", ci);
        var sweep = NazcaCoordinateMapper.NormalizeZero(-bend.SweepAngleDegrees).ToString("F2", ci);

        sb.AppendLine($"_metal = c.add_ref(gf.path.arc(radius={radius}, angle={sweep})"
                      + ".extrude(width=METAL_WIDTH, layer=METAL_LAYER))");
        AppendPlacement(sb, -bend.StartAngleDegrees, sx, sy, ci);
    }

    private static void AppendPlacement(StringBuilder sb, double angleDeg, double x, double y, CultureInfo ci)
    {
        var a = NazcaCoordinateMapper.NormalizeZero(angleDeg).ToString("F2", ci);
        var px = NazcaCoordinateMapper.NormalizeZero(x).ToString("F2", ci);
        var py = NazcaCoordinateMapper.NormalizeZero(y).ToString("F2", ci);
        sb.AppendLine($"_metal.rotate({a})");
        sb.AppendLine($"_metal.move(({px}, {py}))");
    }
}
