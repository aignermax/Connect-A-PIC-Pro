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
    /// <summary>Below this |turn| (degrees) two pins count as parallel: a single bend cannot
    /// join them, so a parallel <see cref="WaveguideType.Bend"/> falls back to an S-bend.</summary>
    private const double ParallelTurnThresholdDegrees = 1.0;

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
            // SBend (nd.sinebend) and Cobra (nd.cobra) are point-to-point primitives. The exact
            // sine / cobra curve is APPROXIMATED by a symmetric S-bend (arc–straight–arc) that
            // shifts by the same lateral offset over the same forward distance and arrives
            // parallel to the start heading — the identical (distance, offset) basis the exporter
            // feeds to nd.sinebend, and the parallel-arrival case of nd.cobra.
            WaveguideType.SBend => BuildPointToPoint(sx, sy, startAngle, ex, ey, radius),
            WaveguideType.Cobra => BuildPointToPoint(sx, sy, startAngle, ex, ey, radius),
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

        if (Math.Abs(sweep) < ParallelTurnThresholdDegrees)
        {
            // Parallel pins: a single arc cannot bridge a lateral offset and still arrive at the
            // pin angle (physically impossible). Fall back to a symmetric S-bend — the valid way
            // to connect two parallel offset pins. Note: nd.bend(angle≈0) is degenerate for such
            // a layout, so Bend is meant for pins that face each other at an angle.
            var (longitudinal, lateral) = LocalFrame(sx, sy, ex, ey, startAngle);
            var sBend = SBendGeometry.BuildSymmetricS(sx, sy, startAngle, longitudinal, lateral, radius);
            if (sBend != null)
            {
                foreach (var segment in sBend)
                    path.Segments.Add(segment);
                return path;
            }
            // No lateral offset either → the pins are collinear: a straight along the heading.
            path.Segments.Add(new StraightSegment(sx, sy, ex, ey, startAngle));
            return path;
        }

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
                                                double ex, double ey, double radius)
    {
        var path = new RoutedPath();
        var (longitudinal, lateral) = LocalFrame(sx, sy, ex, ey, startAngle);

        var sBend = SBendGeometry.BuildSymmetricS(sx, sy, startAngle, longitudinal, lateral, radius);
        if (sBend != null)
        {
            foreach (var segment in sBend)
                path.Segments.Add(segment);
            return path;
        }

        // Degenerate: end pin behind the start, or the offset is too small to warrant an arc.
        // A direct straight still visibly connects the pins (near-collinear, so barely diagonal).
        double directAngle = Math.Atan2(ey - sy, ex - sx) * 180.0 / Math.PI;
        path.Segments.Add(new StraightSegment(sx, sy, ex, ey, directAngle));
        return path;
    }

    /// <summary>
    /// End-pin displacement expressed in the start pin's frame: longitudinal (along the start
    /// heading) and signed lateral (perpendicular, positive toward increasing heading). Matches
    /// the (distance, offset) basis of <c>NazcaConnectionStyleWriter</c>.
    /// </summary>
    private static (double Longitudinal, double Lateral) LocalFrame(
        double sx, double sy, double ex, double ey, double startAngle)
    {
        double dx = ex - sx;
        double dy = ey - sy;
        double rad = -startAngle * Math.PI / 180.0;
        double longitudinal = dx * Math.Cos(rad) - dy * Math.Sin(rad);
        double lateral = dx * Math.Sin(rad) + dy * Math.Cos(rad);
        return (longitudinal, lateral);
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
