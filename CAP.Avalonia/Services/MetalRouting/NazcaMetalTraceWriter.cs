using System.Globalization;
using System.Text;
using CAP_Core.Components.Core;
using CAP_Core.Export;
using CAP_Core.Routing;
using CAP_Core.Routing.MetalRouting;

namespace CAP.Avalonia.Services.MetalRouting;

/// <summary>
/// Emits electrical connections as metal traces into the Nazca export script (issue #682).
/// Traces use the process-derived width and GDS layer (<see cref="MetalRoutingSpec"/>);
/// where the process requires bridges, a bridge marker polygon is placed at every
/// metal/waveguide crossing point. Coordinate handling mirrors the optical segment
/// export (<see cref="NazcaCoordinateMapper"/> plain Y negation).
/// </summary>
public static class NazcaMetalTraceWriter
{
    /// <summary>Bridge marker square edge length as a multiple of the metal trace width.</summary>
    private const double BridgeSizeFactor = 2.0;

    /// <summary>
    /// Appends the metal routing constants to the script header so all traces,
    /// pads, and bridges reference one culture-invariant definition.
    /// </summary>
    public static void AppendHeaderConstants(StringBuilder sb, MetalRoutingSpec spec)
    {
        var ci = CultureInfo.InvariantCulture;
        sb.AppendLine("# Electrical metal routing (process-derived)");
        sb.AppendLine($"METAL_WIDTH = {spec.TraceWidthMicrometers.ToString("F2", ci)}  # Metal trace width in µm");
        sb.AppendLine($"METAL_LAYER = ({spec.MetalGdsLayer.ToString(ci)}, {spec.MetalGdsDatatype.ToString(ci)})");
        sb.AppendLine($"BRIDGE_LAYER = ({spec.BridgeGdsLayer.ToString(ci)}, 0)");
        sb.AppendLine();
    }

    /// <summary>
    /// Appends one electrical connection as metal trace segments. Falls back to a
    /// single pin-to-pin straight when the connection carries no routed path.
    /// </summary>
    public static void AppendMetalConnection(
        StringBuilder sb, IReadOnlyList<PathSegment> segments,
        PhysicalPin? startPin, PhysicalPin? endPin)
    {
        // Route-less or single-straight connections: compute the trace directly from the
        // pin positions so the metal hits both pads exactly (mirrors the optical export).
        bool isSingleStraight = segments.Count == 0
            || (segments.Count == 1 && segments[0] is StraightSegment);
        if (isSingleStraight && startPin != null && endPin != null)
        {
            var (sx, sy) = NazcaCoordinateMapper.GetPinNazcaPosition(startPin);
            var (ex, ey) = NazcaCoordinateMapper.GetPinNazcaPosition(endPin);
            sb.AppendLine(FormatStraight(sx, sy, ex, ey));
            return;
        }
        if (segments.Count == 0)
            return;

        foreach (var segment in segments)
        {
            var (nsx, nsy) = NazcaCoordinateMapper.ToNazca(segment.StartPoint.X, segment.StartPoint.Y);
            var (nex, ney) = NazcaCoordinateMapper.ToNazca(segment.EndPoint.X, segment.EndPoint.Y);
            sb.AppendLine(segment switch
            {
                StraightSegment => FormatStraight(nsx, nsy, nex, ney),
                BendSegment bend => FormatBend(bend, nsx, nsy),
                _ => $"        # Unknown segment type: {segment.GetType().Name}"
            });
        }
    }

    /// <summary>
    /// Appends a bridge marker polygon on the bridge layer at every crossing point
    /// (app coordinates). Called only when the process requires bridges.
    /// </summary>
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
            sb.AppendLine($"        # BRIDGE: metal crosses waveguide at ({NazcaCoordinateMapper.NormalizeZero(cx).ToString("F2", ci)}, {NazcaCoordinateMapper.NormalizeZero(cy).ToString("F2", ci)})");
            sb.AppendLine($"        nd.Polygon(points=[({x0},{y0}),({x1},{y0}),({x1},{y1}),({x0},{y1})], layer=BRIDGE_LAYER).put(0, 0)");
        }
    }

    /// <summary>Formats a straight metal trace from absolute Nazca start/end positions.</summary>
    private static string FormatStraight(double startX, double startY, double endX, double endY)
    {
        var ci = CultureInfo.InvariantCulture;
        double dx = endX - startX;
        double dy = endY - startY;
        double length = Math.Sqrt(dx * dx + dy * dy);
        double angleDeg = Math.Atan2(dy, dx) * 180.0 / Math.PI;

        var l = length.ToString("F2", ci);
        var x = NazcaCoordinateMapper.NormalizeZero(startX).ToString("F2", ci);
        var y = NazcaCoordinateMapper.NormalizeZero(startY).ToString("F2", ci);
        var a = NazcaCoordinateMapper.NormalizeZero(angleDeg).ToString("F2", ci);
        return $"        nd.strt(length={l}, width=METAL_WIDTH, layer=METAL_LAYER).put({x}, {y}, {a})";
    }

    /// <summary>Formats a bend metal trace; angles are negated for the Y-flip like the optical export.</summary>
    private static string FormatBend(BendSegment bend, double nazcaX, double nazcaY)
    {
        var ci = CultureInfo.InvariantCulture;
        var radius = bend.RadiusMicrometers.ToString("F2", ci);
        var sweep = NazcaCoordinateMapper.NormalizeZero(-bend.SweepAngleDegrees).ToString("F2", ci);
        var x = NazcaCoordinateMapper.NormalizeZero(nazcaX).ToString("F2", ci);
        var y = NazcaCoordinateMapper.NormalizeZero(nazcaY).ToString("F2", ci);
        var angle = NazcaCoordinateMapper.NormalizeZero(-bend.StartAngleDegrees).ToString("F2", ci);
        return $"        nd.bend(radius={radius}, angle={sweep}, width=METAL_WIDTH, layer=METAL_LAYER).put({x}, {y}, {angle})";
    }
}
