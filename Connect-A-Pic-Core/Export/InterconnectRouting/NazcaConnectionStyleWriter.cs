using System.Globalization;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Routing.InterconnectRouting;

namespace CAP_Core.Export.InterconnectRouting;

/// <summary>
/// Emits a single Nazca primitive for a connection with an explicit point-to-point routing
/// style (SBend, Cobra). The primitive is placed absolutely at the start pin's Nazca
/// position/angle; point-to-point styles (sinebend, cobra) receive the end pin expressed in the
/// start pin's local frame.
///
/// Bend returns null on purpose: a single <c>nd.bend</c> is parameterized only by
/// (radius, angle) and therefore CANNOT land on an arbitrary end pin — exporting one would
/// leave the GDS physically disconnected. That style is exported through the segment exporter
/// (<c>SimpleNazcaExporter.AppendSegmentExport</c>), which writes exactly the canvas arc
/// segments built by <c>ConnectionStyleRouteBuilder</c>, so the exported geometry reaches
/// both pins and matches the canvas by construction.
/// </summary>
public static class NazcaConnectionStyleWriter
{
    private const string Indent = "        ";
    private const double DegreesToRadians = Math.PI / 180.0;

    /// <summary>
    /// Formats the Nazca export line for a styled point-to-point connection, or null when the
    /// connection is handled by the segment exporter instead: Auto (routed segments) as well as
    /// Bend, whose single-primitive form cannot reach an arbitrary end pin (see class
    /// remarks) — its exact canvas segments are the exported truth.
    /// </summary>
    /// <param name="connection">Connection carrying style, width and bend radius.</param>
    /// <param name="gdsLayer">Optional GDS layer appended to the primitive call.</param>
    /// <param name="sourceLayer">
    /// Optional import source (layer, datatype) tag — emitted as a <c>layer=(L, D)</c>
    /// tuple and wins over <paramref name="gdsLayer"/>: manufacturing needs the original
    /// layer back, not the process default.
    /// </param>
    /// <returns>A single Python line, or null for segment-exported styles.</returns>
    public static string? Format(
        WaveguideConnection connection, int? gdsLayer = null, (int Layer, int DataType)? sourceLayer = null)
    {
        if (connection.Type is WaveguideType.Auto or WaveguideType.Bend ||
            connection.StartPin == null || connection.EndPin == null)
            return null;

        var geometry = ComputeLocalGeometry(connection.StartPin, connection.EndPin);

        // nd.sinebend needs a positive forward run; the degenerate canvas fallback
        // (end pin behind the start) is exported as its exact segments instead.
        if (connection.Type == WaveguideType.SBend && geometry.LocalDx <= 0)
            return null;

        var ci = CultureInfo.InvariantCulture;
        // The endpoint pins' PDK-stamped width is the process truth; the connection's own
        // width is the fallback for unstamped (demo/playground) pins.
        var widthUm = connection.StartPin.WaveguideWidthMicrometers
            ?? connection.EndPin.WaveguideWidthMicrometers
            ?? connection.WidthMicrometers;
        string w = widthUm.ToString("F2", ci);
        string layer = sourceLayer is { } s
            ? $", layer=({s.Layer.ToString(ci)}, {s.DataType.ToString(ci)})"
            : gdsLayer.HasValue ? $", layer={gdsLayer.Value}" : string.Empty;
        string primitive = FormatPrimitive(connection.Type, geometry, w, layer, ci);
        return $"{Indent}{primitive}.put({Fmt(geometry.StartX, ci)}, {Fmt(geometry.StartY, ci)}, {Fmt(geometry.StartAngle, ci)})";
    }

    private static string FormatPrimitive(
        WaveguideType type, LocalGeometry g, string w, string layer, CultureInfo ci)
    {
        return type switch
        {
            WaveguideType.SBend => $"nd.sinebend(width={w}, distance={Fmt(g.LocalDx, ci)}, offset={Fmt(g.LocalDy, ci)}{layer})",
            WaveguideType.Cobra =>
                $"nd.cobra(xya=({Fmt(g.LocalDx, ci)}, {Fmt(g.LocalDy, ci)}, {Fmt(g.LocalDa, ci)}), width1={w}, width2={w}{layer})",
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported waveguide style."),
        };
    }

    /// <summary>End-pin geometry expressed in the start pin's Nazca frame.</summary>
    private readonly record struct LocalGeometry(
        double StartX, double StartY, double StartAngle,
        double LocalDx, double LocalDy, double LocalDa);

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
            NormalizeSigned(arrivalAngle - startAngle));
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
