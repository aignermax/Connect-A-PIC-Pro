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
    /// <summary>Arc samples used to approximate a bend as a metal ribbon polygon (issue #686 review).</summary>
    private const int MetalBendArcSampleCount = 16;

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
    /// generic 'strip' cross-section (#570 field-test fix). Ignored when <paramref name="metal"/>
    /// is set.</param>
    /// <param name="metal">Width/layer of an electrical (metal) trace (issue #682). When set, the
    /// segment is drawn as a polygon on the metal layer instead of a routed waveguide cell —
    /// gdsfactory's <c>straight()</c>/<c>bend_circular()</c> factories have no <c>layer=</c>
    /// kwarg (verified <c>TypeError</c> against the installed gdsfactory, issue #686 review).</param>
    public static void AppendSegments(
        StringBuilder sb, IReadOnlyList<PathSegment> segments,
        PhysicalPin? startPin = null, PhysicalPin? endPin = null,
        string waveguideKwarg = "width=WG_WIDTH", MetalTraceStyle? metal = null)
    {
        if (segments.Count == 1 && segments[0] is StraightSegment && startPin != null && endPin != null)
        {
            var (sx, sy) = NazcaCoordinateMapper.GetPinNazcaPosition(startPin);
            var (ex, ey) = NazcaCoordinateMapper.GetPinNazcaPosition(endPin);
            AppendStraightOrMetal(sb, sx, sy, ex, ey, waveguideKwarg, metal);
            return;
        }

        foreach (var segment in segments)
        {
            var (nsx, nsy) = NazcaCoordinateMapper.ToNazca(segment.StartPoint.X, segment.StartPoint.Y);
            switch (segment)
            {
                case StraightSegment:
                    var (nex, ney) = NazcaCoordinateMapper.ToNazca(segment.EndPoint.X, segment.EndPoint.Y);
                    AppendStraightOrMetal(sb, nsx, nsy, nex, ney, waveguideKwarg, metal);
                    break;
                case BendSegment bend:
                    if (metal != null)
                        AppendMetalPolygon(sb, SampleBendCenterlineNazca(bend), metal);
                    else
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
        string waveguideKwarg = "width=WG_WIDTH", MetalTraceStyle? metal = null)
    {
        var (sx, sy) = NazcaCoordinateMapper.GetPinNazcaPosition(startPin);
        var (ex, ey) = NazcaCoordinateMapper.GetPinNazcaPosition(endPin);
        sb.AppendLine("# routeless connection: direct pin-to-pin straight");
        AppendStraightOrMetal(sb, sx, sy, ex, ey, waveguideKwarg, metal);
    }

    /// <summary>
    /// Emits a routed straight: an optical waveguide via <c>gf.components.straight()</c> when
    /// <paramref name="metal"/> is null, otherwise a metal-layer rectangle via
    /// <c>add_polygon</c> (issue #682/#686 review).
    /// </summary>
    private static void AppendStraightOrMetal(
        StringBuilder sb, double sx, double sy, double ex, double ey, string waveguideKwarg, MetalTraceStyle? metal)
    {
        if (metal != null)
            AppendMetalPolygon(sb, new[] { (sx, sy), (ex, ey) }, metal);
        else
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

    /// <summary>
    /// Draws a metal trace as a rectangle/ribbon polygon directly on the process metal layer via
    /// <c>c.add_polygon(points, layer=(L, D))</c> — mirroring how <see cref="GdsFactoryStubWriter"/>
    /// emits stub rectangles — instead of a routed gdsfactory cell. gdsfactory's
    /// <c>straight()</c>/<c>bend_circular()</c> factories reject an unknown <c>layer=</c> kwarg
    /// at run time, so a metal trace cannot reuse them (issue #682/#686 review).
    /// </summary>
    private static void AppendMetalPolygon(
        StringBuilder sb, IReadOnlyList<(double X, double Y)> centerline, MetalTraceStyle metal)
    {
        if (centerline.Count < 2)
            return;

        var ci = CultureInfo.InvariantCulture;
        var halfWidth = metal.WidthUm / 2.0;
        var polygon = BuildRibbonPolygon(centerline, halfWidth);
        var points = string.Join(", ", polygon.Select(p => FormatPoint(p, ci)));
        sb.AppendLine($"c.add_polygon([{points}], layer={metal.LayerTuple})");
    }

    /// <summary>
    /// Offsets a centerline (2+ points) into a closed ribbon polygon of the given half-width:
    /// the left-side offsets forward, then the right-side offsets reversed.
    /// </summary>
    private static IReadOnlyList<(double X, double Y)> BuildRibbonPolygon(
        IReadOnlyList<(double X, double Y)> centerline, double halfWidth)
    {
        var left = new List<(double X, double Y)>();
        var right = new List<(double X, double Y)>();
        for (int i = 0; i < centerline.Count - 1; i++)
        {
            var (x0, y0) = centerline[i];
            var (x1, y1) = centerline[i + 1];
            double dx = x1 - x0;
            double dy = y1 - y0;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len <= 0)
                continue;

            double ox = -dy / len * halfWidth;
            double oy = dx / len * halfWidth;
            left.Add((x0 + ox, y0 + oy));
            left.Add((x1 + ox, y1 + oy));
            right.Add((x0 - ox, y0 - oy));
            right.Add((x1 - ox, y1 - oy));
        }

        right.Reverse();
        left.AddRange(right);
        return left;
    }

    /// <summary>
    /// Samples a bend's arc (in app coordinates, using the same geometry <see cref="BendSegment"/>
    /// derives its own start/end points from) into Nazca-space centerline points for the metal
    /// ribbon polygon (issue #686 review).
    /// </summary>
    private static IReadOnlyList<(double X, double Y)> SampleBendCenterlineNazca(BendSegment bend)
    {
        var points = new List<(double X, double Y)>(MetalBendArcSampleCount + 1);
        var sign = Math.Sign(bend.SweepAngleDegrees);
        for (int i = 0; i <= MetalBendArcSampleCount; i++)
        {
            double t = (double)i / MetalBendArcSampleCount;
            double angleDeg = bend.StartAngleDegrees + t * bend.SweepAngleDegrees;
            double angleRad = angleDeg * Math.PI / 180.0 - Math.PI / 2.0 * sign;
            double appX = bend.Center.X + bend.RadiusMicrometers * Math.Cos(angleRad);
            double appY = bend.Center.Y + bend.RadiusMicrometers * Math.Sin(angleRad);
            points.Add(NazcaCoordinateMapper.ToNazca(appX, appY));
        }
        return points;
    }

    private static string FormatPoint((double X, double Y) point, CultureInfo ci) =>
        $"({NazcaCoordinateMapper.NormalizeZero(point.X).ToString("F2", ci)}, " +
        $"{NazcaCoordinateMapper.NormalizeZero(point.Y).ToString("F2", ci)})";
}
