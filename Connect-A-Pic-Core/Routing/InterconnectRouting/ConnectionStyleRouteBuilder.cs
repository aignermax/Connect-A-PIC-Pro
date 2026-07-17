using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Routing.AStarPathfinder;

namespace CAP_Core.Routing.InterconnectRouting;

/// <summary>
/// Builds the visible primitive geometry for a connection whose <see cref="WaveguideType"/>
/// is an explicit style (anything but <see cref="WaveguideType.Auto"/>).
///
/// The geometry is produced in app-space (Y-down, the frame the canvas renderer and the A*
/// router use) from the SAME basis the Nazca exporter consumes — start pin position/angle,
/// pin-to-pin distance and the end pin's arrival frame — so the drawn curve matches the
/// exported primitive (<c>NazcaConnectionStyleWriter</c>): the exporter parameterizes each
/// primitive from the start pin and emits it in Nazca space (Y-up), i.e. the Y-mirror of the
/// identical physical curve. Distance, radius and turn magnitude are therefore shared.
///
/// The route is forced: it follows the user's chosen style and deliberately ignores
/// obstacles (only Auto avoids them).
/// </summary>
public static class ConnectionStyleRouteBuilder
{
    /// <summary>
    /// Builds the styled route between two pins.
    /// </summary>
    /// <param name="startPin">Source pin; the primitive starts here at the pin angle.</param>
    /// <param name="endPin">Target pin; point-to-point styles arrive here at its input angle.</param>
    /// <param name="type">The explicit routing style (must not be <see cref="WaveguideType.Auto"/>).</param>
    /// <param name="bendRadiusMicrometers">
    /// Bend radius in micrometers (comes automatically from the interconnect defaults, no UI);
    /// falls back to <see cref="InterconnectSettings.DefaultBendRadiusMicrometers"/> when non-positive.
    /// </param>
    /// <returns>A routed path in app-space coordinates.</returns>
    public static RoutedPath Build(PhysicalPin startPin, PhysicalPin endPin,
                                   WaveguideType type, double bendRadiusMicrometers)
    {
        var (sx, sy) = startPin.GetAbsolutePosition();
        var (ex, ey) = endPin.GetAbsolutePosition();
        double startAngle = startPin.GetAbsoluteAngle();
        // The waveguide arrives INTO the end pin, so the arrival direction is the end pin's
        // outward direction rotated by 180° (mirrors NazcaConnectionStyleWriter).
        double arrivalAngle = endPin.GetAbsoluteAngle() + 180.0;
        double distance = Distance(sx, sy, ex, ey);
        double radius = bendRadiusMicrometers > 0
            ? bendRadiusMicrometers
            : InterconnectSettings.DefaultBendRadiusMicrometers;

        return type switch
        {
            WaveguideType.Straight => BuildStraight(sx, sy, startAngle, distance),
            // Bend is a single circular arc (nd.bend). Euler (nd.euler) is a clothoid with
            // adiabatic curvature; an exact clothoid is out of scope, so it is APPROXIMATED
            // by the same circular arc of the given radius and turn angle — visibly a bend of
            // the correct radius and sweep, sharing endpoints/basis with the exported primitive.
            WaveguideType.Bend => BuildArc(sx, sy, startAngle, arrivalAngle, radius, ex, ey),
            WaveguideType.Euler => BuildArc(sx, sy, startAngle, arrivalAngle, radius, ex, ey),
            // SBend (nd.sinebend) and Cobra (nd.cobra) are point-to-point primitives that reach
            // the end pin at its arrival angle. The exact sine / cobra curve is APPROXIMATED by
            // the router's approach S-bend (bend–straight–bend), which reaches the identical end
            // point and arrival angle — same endpoints/radius basis, curve shape approximated.
            WaveguideType.SBend => BuildPointToPoint(sx, sy, startAngle, ex, ey, arrivalAngle, radius),
            WaveguideType.Cobra => BuildPointToPoint(sx, sy, startAngle, ex, ey, arrivalAngle, radius),
            _ => BuildStraight(sx, sy, startAngle, distance),
        };
    }

    private static RoutedPath BuildStraight(double sx, double sy, double startAngle, double distance)
    {
        // nd.strt(length=distance).put(start, startAngle): a straight run along the start pin
        // angle for the pin-to-pin distance.
        double rad = startAngle * Math.PI / 180.0;
        double endX = sx + distance * Math.Cos(rad);
        double endY = sy + distance * Math.Sin(rad);
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(sx, sy, endX, endY, startAngle));
        return path;
    }

    private static RoutedPath BuildArc(double sx, double sy, double startAngle,
                                       double arrivalAngle, double radius, double ex, double ey)
    {
        double sweep = NormalizeSigned(arrivalAngle - startAngle);
        var path = new RoutedPath();
        var builder = new BendBuilder(radius);
        var bend = builder.BuildBend(sx, sy, startAngle, startAngle + sweep, BendMode.Flexible, radius);
        if (bend != null)
            path.Segments.Add(bend);
        else
            // Turn angle is negligible: a straight run to the end pin represents nd.bend(angle≈0).
            path.Segments.Add(new StraightSegment(sx, sy, ex, ey, startAngle));
        return path;
    }

    private static RoutedPath BuildPointToPoint(double sx, double sy, double startAngle,
                                                double ex, double ey, double arrivalAngle, double radius)
    {
        var path = new RoutedPath();
        var bendBuilder = new BendBuilder(radius);
        var sbend = new SBendBuilder(bendBuilder, radius);
        double x = sx, y = sy, angle = startAngle;

        if (!sbend.TryBuildApproachSBend(path, ref x, ref y, ref angle, ex, ey, arrivalAngle)
            || path.Segments.Count == 0)
        {
            // Too little room for a two-bend approach: fall back to a direct straight so the
            // curve still visibly connects the pins.
            path.Segments.Clear();
            double directAngle = Math.Atan2(ey - sy, ex - sx) * 180.0 / Math.PI;
            path.Segments.Add(new StraightSegment(sx, sy, ex, ey, directAngle));
        }
        return path;
    }

    private static double Distance(double x1, double y1, double x2, double y2)
    {
        double dx = x2 - x1;
        double dy = y2 - y1;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Normalizes an angle in degrees to the range (-180, 180].</summary>
    private static double NormalizeSigned(double angleDegrees)
    {
        double a = angleDegrees % 360.0;
        if (a > 180.0) a -= 360.0;
        if (a <= -180.0) a += 360.0;
        return a;
    }
}
