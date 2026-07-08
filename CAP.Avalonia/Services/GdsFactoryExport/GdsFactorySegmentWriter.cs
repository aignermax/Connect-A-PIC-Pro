using System.Globalization;
using System.Text;
using CAP_Core.Components.Core;
using CAP_Core.Export;
using CAP_Core.Routing;

namespace CAP.Avalonia.Services.GdsFactoryExport;

/// <summary>
/// Emits routed waveguide segments as absolutely placed gdsfactory references.
/// The coordinate contract is identical to the Nazca segment export (both targets
/// are Y-up): geometry converts via <see cref="NazcaCoordinateMapper.ToNazca"/> and
/// angles are negated. Emitted lines reference the module-level design component
/// variable <c>c</c> defined by <see cref="GdsFactoryExporter"/>.
/// </summary>
public static class GdsFactorySegmentWriter
{
    /// <summary>
    /// Appends all segments of one routed connection. A single straight segment with
    /// known pins is emitted pin-to-pin (exact endpoints) like the Nazca export.
    /// </summary>
    /// <param name="sb">Target script builder.</param>
    /// <param name="segments">Routed path segments in editor (app) coordinates.</param>
    /// <param name="startPin">Start pin, used for single-straight pin-to-pin geometry.</param>
    /// <param name="endPin">End pin, used for single-straight pin-to-pin geometry.</param>
    /// <param name="waveguideKwarg">The waveguide-sizing keyword argument emitted into every
    /// routed straight/bend — <c>width=WG_WIDTH</c> for Nazca/generic designs, or
    /// <c>cross_section='xs_nc'</c> for a gdsfactory-native PDK whose activated PDK has no
    /// generic 'strip' cross-section (#570 field-test fix).</param>
    public static void AppendSegments(
        StringBuilder sb, IReadOnlyList<PathSegment> segments,
        PhysicalPin? startPin = null, PhysicalPin? endPin = null,
        string waveguideKwarg = "width=WG_WIDTH")
    {
        if (segments.Count == 1 && segments[0] is StraightSegment && startPin != null && endPin != null)
        {
            var (sx, sy) = NazcaCoordinateMapper.GetPinNazcaPosition(startPin);
            var (ex, ey) = NazcaCoordinateMapper.GetPinNazcaPosition(endPin);
            AppendStraight(sb, sx, sy, ex, ey, waveguideKwarg);
            return;
        }

        foreach (var segment in segments)
        {
            var (nsx, nsy) = NazcaCoordinateMapper.ToNazca(segment.StartPoint.X, segment.StartPoint.Y);
            switch (segment)
            {
                case StraightSegment:
                    var (nex, ney) = NazcaCoordinateMapper.ToNazca(segment.EndPoint.X, segment.EndPoint.Y);
                    AppendStraight(sb, nsx, nsy, nex, ney, waveguideKwarg);
                    break;
                case BendSegment bend:
                    AppendBend(sb, bend, nsx, nsy, waveguideKwarg);
                    break;
                default:
                    sb.AppendLine($"# Unknown segment type: {segment.GetType().Name}");
                    break;
            }
        }
    }

    /// <summary>
    /// Appends the pin-to-pin fallback for connections without routed segments —
    /// a direct straight between both pin positions (no auto-routing in v1).
    /// </summary>
    public static void AppendPinToPinFallback(
        StringBuilder sb, PhysicalPin startPin, PhysicalPin endPin,
        string waveguideKwarg = "width=WG_WIDTH")
    {
        var (sx, sy) = NazcaCoordinateMapper.GetPinNazcaPosition(startPin);
        var (ex, ey) = NazcaCoordinateMapper.GetPinNazcaPosition(endPin);
        sb.AppendLine("# routeless connection: direct pin-to-pin straight");
        AppendStraight(sb, sx, sy, ex, ey, waveguideKwarg);
    }

    private static void AppendStraight(
        StringBuilder sb, double sx, double sy, double ex, double ey, string waveguideKwarg)
    {
        var ci = CultureInfo.InvariantCulture;
        double dx = ex - sx;
        double dy = ey - sy;
        double length = Math.Sqrt(dx * dx + dy * dy);
        double angleDeg = Math.Atan2(dy, dx) * 180.0 / Math.PI;

        sb.AppendLine($"_seg = c.add_ref(gf.components.straight(length={length.ToString("F2", ci)}, {waveguideKwarg}))");
        AppendPlacement(sb, angleDeg, sx, sy, ci);
    }

    private static void AppendBend(StringBuilder sb, BendSegment bend, double sx, double sy, string waveguideKwarg)
    {
        var ci = CultureInfo.InvariantCulture;
        var radius = bend.RadiusMicrometers.ToString("F2", ci);
        var sweep = NazcaCoordinateMapper.NormalizeZero(-bend.SweepAngleDegrees).ToString("F2", ci);

        sb.AppendLine($"_seg = c.add_ref(gf.components.bend_circular(radius={radius}, angle={sweep}, "
                      + $"{waveguideKwarg}, allow_min_radius_violation=True))");
        AppendPlacement(sb, -bend.StartAngleDegrees, sx, sy, ci);
    }

    private static void AppendPlacement(StringBuilder sb, double angleDeg, double x, double y, CultureInfo ci)
    {
        var a = NazcaCoordinateMapper.NormalizeZero(angleDeg).ToString("F2", ci);
        var px = NazcaCoordinateMapper.NormalizeZero(x).ToString("F2", ci);
        var py = NazcaCoordinateMapper.NormalizeZero(y).ToString("F2", ci);
        sb.AppendLine($"_seg.rotate({a})");
        sb.AppendLine($"_seg.move(({px}, {py}))");
    }
}
