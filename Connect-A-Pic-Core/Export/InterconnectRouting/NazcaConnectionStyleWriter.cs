using System.Globalization;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Routing.InterconnectRouting;

namespace CAP_Core.Export.InterconnectRouting;

/// <summary>
/// Emits a single Nazca primitive for a connection with an explicit point-to-point routing
/// style (Straight, SBend, Cobra). The primitive is placed absolutely at the start pin's Nazca
/// position/angle; point-to-point styles (sinebend, cobra) receive the end pin expressed in the
/// start pin's local frame.
///
/// Bend and Euler return null on purpose: a single <c>nd.bend</c>/<c>nd.euler</c> is
/// parameterized only by (radius, angle) and therefore CANNOT land on an arbitrary end pin —
/// exporting one would leave the GDS physically disconnected. Those styles are exported through
/// the segment exporter (<c>SimpleNazcaExporter.AppendSegmentExport</c>), which writes exactly
/// the canvas arc segments built by <c>ConnectionStyleRouteBuilder</c>, so the exported
/// geometry reaches both pins and matches the canvas by construction. Straight likewise
/// returns null for laterally offset pins, whose canvas route is the connected arc-S fallback.
/// </summary>
public static class NazcaConnectionStyleWriter
{
    private const string Indent = "        ";
    private const double DegreesToRadians = Math.PI / 180.0;

    /// <summary>
    /// Formats the Nazca export line for a styled point-to-point connection, or null when the
    /// connection is handled by the segment exporter instead: Auto (routed segments) as well as
    /// Bend and Euler, whose single-primitive form cannot reach an arbitrary end pin (see class
    /// remarks) — their exact canvas segments are the exported truth.
    /// </summary>
    /// <param name="connection">Connection carrying style, width and bend radius.</param>
    /// <param name="gdsLayer">Optional GDS layer appended to the primitive call.</param>
    /// <returns>A single Python line, or null for segment-exported styles.</returns>
    public static string? Format(WaveguideConnection connection, int? gdsLayer = null)
    {
        if (connection.Type is WaveguideType.Auto or WaveguideType.Bend or WaveguideType.Euler ||
            connection.StartPin == null || connection.EndPin == null)
            return null;

        var geometry = ComputeLocalGeometry(connection.StartPin, connection.EndPin);

        // Straight only has a single-primitive form for (nearly) collinear pins; an offset
        // Straight falls back to the arc-S on canvas (ConnectionStyleRouteBuilder) and must be
        // exported as those exact segments — an nd.strt would end in mid-air. Same threshold
        // as the canvas so canvas and GDS always agree.
        if (connection.Type == WaveguideType.Straight && !IsAlignedForward(geometry))
            return null;

        // nd.sinebend needs a positive forward run; the degenerate canvas fallback
        // (end pin behind the start) is exported as its exact segments instead.
        if (connection.Type == WaveguideType.SBend && geometry.LocalDx <= 0)
            return null;

        var ci = CultureInfo.InvariantCulture;
        string w = connection.WidthMicrometers.ToString("F2", ci);
        string layer = gdsLayer.HasValue ? $", layer={gdsLayer.Value}" : string.Empty;
        string primitive = FormatPrimitive(connection.Type, geometry, w, layer, ci);
        return $"{Indent}{primitive}.put({Fmt(geometry.StartX, ci)}, {Fmt(geometry.StartY, ci)}, {Fmt(geometry.StartAngle, ci)})";
    }

    private static string FormatPrimitive(
        WaveguideType type, LocalGeometry g, string w, string layer, CultureInfo ci)
    {
        return type switch
        {
            WaveguideType.Straight => $"nd.strt(length={Fmt(g.Distance, ci)}, width={w}{layer})",
            WaveguideType.SBend => $"nd.sinebend(width={w}, distance={Fmt(g.LocalDx, ci)}, offset={Fmt(g.LocalDy, ci)}{layer})",
            WaveguideType.Cobra =>
                $"nd.cobra(xya=({Fmt(g.LocalDx, ci)}, {Fmt(g.LocalDy, ci)}, {Fmt(g.LocalDa, ci)}), width1={w}, width2={w}{layer})",
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported waveguide style."),
        };
    }

    /// <summary>True when the end pin lies ahead of the start pin and (nearly) on its axis,
    /// i.e. the layout a plain <c>nd.strt</c> can actually connect.</summary>
    private static bool IsAlignedForward(LocalGeometry g) =>
        g.LocalDx > 0 &&
        Math.Abs(g.LocalDy) < ConnectionStyleRouteBuilder.StraightAlignmentToleranceMicrometers;

    /// <summary>End-pin geometry expressed in the start pin's Nazca frame.</summary>
    private readonly record struct LocalGeometry(
        double StartX, double StartY, double StartAngle,
        double LocalDx, double LocalDy, double LocalDa, double Distance);

    private static LocalGeometry ComputeLocalGeometry(PhysicalPin startPin, PhysicalPin endPin)
    {
        var (sx, sy) = NazcaCoordinateMapper.GetPinNazcaPosition(startPin);
        var (ex, ey) = NazcaCoordinateMapper.GetPinNazcaPosition(endPin);
        double startAngle = NazcaCoordinateMapper.GetPinNazcaAngle(startPin);
        // The waveguide arrives INTO the end pin, so the arrival direction is the
        // end pin's outward direction rotated by 180 degrees.
        double arrivalAngle = NazcaCoordinateMapper.GetPinNazcaAngle(endPin) + 180.0;

        double dx = ex - sx;
        double dy = ey - sy;
        double rad = -startAngle * DegreesToRadians;
        double localDx = dx * Math.Cos(rad) - dy * Math.Sin(rad);
        double localDy = dx * Math.Sin(rad) + dy * Math.Cos(rad);

        return new LocalGeometry(
            sx, sy, NazcaCoordinateMapper.NormalizeZero(startAngle),
            NazcaCoordinateMapper.NormalizeZero(localDx),
            NazcaCoordinateMapper.NormalizeZero(localDy),
            NormalizeSigned(arrivalAngle - startAngle),
            Math.Sqrt(dx * dx + dy * dy));
    }

    /// <summary>Normalizes an angle in degrees to the range (-180, 180].</summary>
    private static double NormalizeSigned(double angleDegrees)
    {
        double a = angleDegrees % 360.0;
        if (a > 180.0) a -= 360.0;
        if (a <= -180.0) a += 360.0;
        return NazcaCoordinateMapper.NormalizeZero(a);
    }

    private static string Fmt(double value, CultureInfo ci) =>
        NazcaCoordinateMapper.NormalizeZero(value).ToString("F2", ci);
}
