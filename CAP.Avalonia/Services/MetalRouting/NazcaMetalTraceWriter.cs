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
}
