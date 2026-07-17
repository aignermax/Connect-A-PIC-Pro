using System.Globalization;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;

namespace CAP_Core.Export.InterconnectRouting;

/// <summary>
/// Emits a single Nazca primitive for a connection with an explicit routing style
/// (<see cref="WaveguideType"/> other than Auto). The primitive is placed absolutely
/// at the start pin's Nazca position/angle; point-to-point styles (sinebend, cobra)
/// receive the end pin expressed in the start pin's local frame.
/// </summary>
public static class NazcaConnectionStyleWriter
{
    private const string Indent = "        ";
    private const double DegreesToRadians = Math.PI / 180.0;

    /// <summary>
    /// Formats the Nazca export line for a styled connection, or null when the
    /// style is <see cref="WaveguideType.Auto"/> (handled by the segment exporter).
    /// </summary>
    /// <param name="connection">Connection carrying style, width and bend radius.</param>
    /// <param name="gdsLayer">Optional GDS layer appended to the primitive call.</param>
    /// <returns>A single Python line, or null for Auto style.</returns>
    public static string? Format(WaveguideConnection connection, int? gdsLayer = null)
    {
        if (connection.Type == WaveguideType.Auto ||
            connection.StartPin == null || connection.EndPin == null)
            return null;

        var geometry = ComputeLocalGeometry(connection.StartPin, connection.EndPin);
        var ci = CultureInfo.InvariantCulture;
        string w = connection.WidthMicrometers.ToString("F2", ci);
        string r = connection.BendRadiusMicrometers.ToString("F2", ci);
        string layer = gdsLayer.HasValue ? $", layer={gdsLayer.Value}" : string.Empty;
        string primitive = FormatPrimitive(connection.Type, geometry, w, r, layer, ci);
        return $"{Indent}{primitive}.put({Fmt(geometry.StartX, ci)}, {Fmt(geometry.StartY, ci)}, {Fmt(geometry.StartAngle, ci)})";
    }

    private static string FormatPrimitive(
        WaveguideType type, LocalGeometry g, string w, string r, string layer, CultureInfo ci)
    {
        return type switch
        {
            WaveguideType.Straight => $"nd.strt(length={Fmt(g.Distance, ci)}, width={w}{layer})",
            WaveguideType.SBend => $"nd.sinebend(width={w}, distance={Fmt(g.LocalDx, ci)}, offset={Fmt(g.LocalDy, ci)}{layer})",
            WaveguideType.Bend => $"nd.bend(radius={r}, angle={Fmt(g.LocalDa, ci)}, width={w}{layer})",
            WaveguideType.Euler => $"nd.euler(width={w}, radius={r}, angle={Fmt(g.LocalDa, ci)}{layer})",
            WaveguideType.Cobra =>
                $"nd.cobra(xya=({Fmt(g.LocalDx, ci)}, {Fmt(g.LocalDy, ci)}, {Fmt(g.LocalDa, ci)}), width1={w}, width2={w}{layer})",
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported waveguide style."),
        };
    }

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
