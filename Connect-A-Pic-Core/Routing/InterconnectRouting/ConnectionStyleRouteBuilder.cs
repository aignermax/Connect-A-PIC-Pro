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
    private const double DegreesToRadians = Math.PI / 180.0;
    private const double Epsilon = 1e-6;

    /// <summary>Below this |turn| (degrees) two pins count as parallel: a single bend cannot
    /// join them, so a parallel <see cref="WaveguideType.Bend"/> falls back to an S-bend.
    /// Matches the minimum sweep <see cref="BendBuilder.BuildBend"/> accepts.</summary>
    private const double MinArcSweepDegrees = 2.0;

    /// <summary>Above this |turn| the tangent length r·tan(|sweep|/2) diverges and the corner
    /// construction becomes numerically unstable; fall back to the S-bend instead.</summary>
    private const double MaxArcSweepDegrees = 179.0;

    /// <summary>Safety factor applied when clamping the radius to the corner's tangent budget,
    /// so the arc's tangent points stay strictly inside the stub straights.</summary>
    private const double RadiusClampSafety = 0.999;

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
            // Bend and Euler are built as stub–arc–stub: the corner is the intersection of the
            // two pin axes, the arc of the requested radius is inscribed at that corner (clamped
            // when it does not fit) and short straight stubs connect it to BOTH pins EXACTLY.
            // Euler (nd.euler) is a clothoid with adiabatic curvature; an exact clothoid is out
            // of scope, so it is APPROXIMATED by the circular arc of the same radius and turn.
            // The exporter emits these exact segments (see NazcaConnectionStyleWriter), so the
            // canvas curve and the GDS are identical by construction.
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

    /// <summary>
    /// Builds the Bend/Euler route as stub–arc–stub so it reaches BOTH pins exactly.
    /// Degenerate layouts (parallel axes, corner behind a pin) fall back to the analytic
    /// S-bend via <see cref="BuildPointToPoint"/> — never a silent diagonal.
    /// </summary>
    private static RoutedPath BuildArc(double sx, double sy, double startAngle,
                                       double arrivalAngle, double radius, double ex, double ey)
    {
        double sweep = NormalizeSigned(arrivalAngle - startAngle);
        if (Math.Abs(sweep) >= MinArcSweepDegrees && Math.Abs(sweep) <= MaxArcSweepDegrees)
        {
            var arcPath = TryBuildStubArcStub(sx, sy, startAngle, sweep, radius, ex, ey);
            if (arcPath != null)
                return arcPath;
        }

        // Parallel pins (sweep ≈ 0 / 180°) or a corner not ahead of both pins: a single arc
        // cannot join the pins, so fall back to the symmetric S-bend (or a collinear straight).
        return BuildPointToPoint(sx, sy, startAngle, ex, ey, radius);
    }

    /// <summary>
    /// Inscribes a circular arc at the corner C where the start pin's forward axis meets the
    /// end pin's backward axis: solving P1 + t·u1 = C = P2 − s·u2 gives the leg lengths t and s
    /// (both must be positive, i.e. C lies AHEAD of both pins). The arc spans the tangent
    /// length τ = r·tan(|sweep|/2) on each leg — from C − τ·u1 to C + τ·u2, its center
    /// perpendicular to the start heading on the turn side (<see cref="BendBuilder"/>) — and is
    /// framed by straight stubs to P1 and P2, so the route hits both pins exactly. When τ would
    /// overrun a leg, the radius is clamped to min(t, s)·tan(|sweep|/2)⁻¹ (with a small safety
    /// margin) instead of giving up. Returns null when the layout is degenerate.
    /// </summary>
    private static RoutedPath? TryBuildStubArcStub(
        double sx, double sy, double startAngle, double sweep, double radius, double ex, double ey)
    {
        var u1 = UnitVector(startAngle);
        var u2 = UnitVector(startAngle + sweep);
        double det = u1.X * u2.Y - u1.Y * u2.X; // = sin(sweep), guarded by the sweep range
        if (Math.Abs(det) < Epsilon)
            return null;

        double dx = ex - sx;
        double dy = ey - sy;
        double t = (dx * u2.Y - dy * u2.X) / det; // P1 → corner along u1
        double s = (u1.X * dy - u1.Y * dx) / det; // corner → P2 along u2
        if (t <= Epsilon || s <= Epsilon)
            return null;

        double tanHalfSweep = Math.Tan(Math.Abs(sweep) * DegreesToRadians / 2.0);
        double clampedRadius = Math.Min(radius, Math.Min(t, s) * RadiusClampSafety / tanHalfSweep);
        if (clampedRadius <= Epsilon)
            return null;
        double tangent = clampedRadius * tanHalfSweep;

        double cornerX = sx + t * u1.X;
        double cornerY = sy + t * u1.Y;
        double arcStartX = cornerX - tangent * u1.X;
        double arcStartY = cornerY - tangent * u1.Y;

        var bend = new BendBuilder(clampedRadius).BuildBend(
            arcStartX, arcStartY, startAngle, startAngle + sweep, BendMode.Flexible, clampedRadius);
        if (bend == null)
            return null;

        var path = new RoutedPath();
        AppendStubIfMeaningful(path, sx, sy, arcStartX, arcStartY, startAngle);
        path.Segments.Add(bend);
        AppendStubIfMeaningful(path, bend.EndPoint.X, bend.EndPoint.Y, ex, ey, startAngle + sweep);
        return path;
    }

    /// <summary>Adds a straight stub (skipped when shorter than <see cref="Epsilon"/>).</summary>
    private static void AppendStubIfMeaningful(
        RoutedPath path, double fromX, double fromY, double toX, double toY, double angleDegrees)
    {
        double dx = toX - fromX;
        double dy = toY - fromY;
        if (Math.Sqrt(dx * dx + dy * dy) <= Epsilon)
            return;
        path.Segments.Add(new StraightSegment(fromX, fromY, toX, toY, angleDegrees));
    }

    private static (double X, double Y) UnitVector(double angleDegrees)
    {
        double rad = angleDegrees * DegreesToRadians;
        return (Math.Cos(rad), Math.Sin(rad));
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
