using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Routing.AStarPathfinder;

namespace CAP_Core.Routing.InterconnectRouting;

/// <summary>
/// Builds the visible primitive geometry for a connection whose <see cref="WaveguideType"/>
/// is an explicit style (anything but <see cref="WaveguideType.Auto"/>).
///
/// Every style connects the start pin to the end pin EXACTLY, each with a visibly distinct,
/// smooth curve:
/// <list type="bullet">
/// <item><b>SBend</b> — the true sine curve of <c>nd.sinebend</c> as a polyline
/// (<see cref="SineBendGeometry"/>); export stays <c>nd.sinebend</c>, same curve basis.</item>
/// <item><b>Cobra</b> — a cubic Hermite matching position and angle at both ends
/// (<see cref="CobraGeometry"/>); export stays <c>nd.cobra</c>.</item>
/// <item><b>Bend / Euler</b> — circular-arc geometry with a GENEROUS radius (0.9 × the largest
/// fitting radius): stub–arc–stub for angled pins, a two-arc S (<see cref="SBendGeometry"/>)
/// for parallel-offset pins. Euler (<c>nd.euler</c>, a clothoid) is APPROXIMATED by the
/// circular arc of the same turn — documented, visually identical to Bend for now.
/// Exported as exact segments, so canvas and GDS match by construction.</item>
/// <item><b>Straight</b> — an exact pin-to-pin straight when the pins are (nearly) collinear
/// (lateral offset &lt; <see cref="StraightAlignmentToleranceMicrometers"/>); otherwise it
/// falls back to the connected arc-S rather than a straight ending in mid-air.</item>
/// </list>
///
/// The route is forced: it follows the user's chosen style and deliberately ignores
/// obstacles (only Auto avoids them). Manual per-bend radius edits
/// (<see cref="WaveguideConnection.BendRadiusOverrides"/>) take precedence — the styled
/// branch of <c>WaveguideConnection.RecalculateTransmission</c> keeps a hand-edited path
/// instead of rebuilding it here.
/// </summary>
public static class ConnectionStyleRouteBuilder
{
    private const double DegreesToRadians = Math.PI / 180.0;
    private const double Epsilon = 1e-6;

    /// <summary>Below this lateral offset (µm) two facing pins count as collinear and the
    /// Straight style draws a true pin-to-pin straight; at or above it, Straight falls back
    /// to the connected arc-S. Shared with <c>NazcaConnectionStyleWriter</c>, which switches
    /// from the <c>nd.strt</c> primitive to exact segment export at the same threshold.</summary>
    public const double StraightAlignmentToleranceMicrometers = 0.5;

    /// <summary>Below this |turn| (degrees) two pins count as parallel: a single bend cannot
    /// join them, so a parallel <see cref="WaveguideType.Bend"/> falls back to an S-bend.
    /// Matches the minimum sweep <see cref="BendBuilder.BuildBend"/> accepts.</summary>
    private const double MinArcSweepDegrees = 2.0;

    /// <summary>Above this |turn| the tangent length r·tan(|sweep|/2) diverges and the corner
    /// construction becomes numerically unstable; fall back to the S-bend instead.</summary>
    private const double MaxArcSweepDegrees = 179.0;

    /// <summary>
    /// Builds the styled route between two pins. All styles reach the end pin exactly.
    /// </summary>
    /// <param name="startPin">Source pin; the primitive starts here at the pin angle.</param>
    /// <param name="endPin">Target pin; every style arrives here (angled styles at its input angle).</param>
    /// <param name="type">The explicit routing style (must not be <see cref="WaveguideType.Auto"/>).</param>
    /// <returns>A routed path in app-space coordinates.</returns>
    public static RoutedPath Build(PhysicalPin startPin, PhysicalPin endPin, WaveguideType type)
    {
        var (sx, sy) = startPin.GetAbsolutePosition();
        var (ex, ey) = endPin.GetAbsolutePosition();
        double startAngle = startPin.GetAbsoluteAngle();
        // The waveguide arrives INTO the end pin, so the arrival direction is the end pin's
        // outward direction rotated by 180° (mirrors NazcaConnectionStyleWriter).
        double arrivalAngle = endPin.GetAbsoluteAngle() + 180.0;

        return type switch
        {
            WaveguideType.Straight => BuildStraight(sx, sy, startAngle, ex, ey),
            WaveguideType.Bend or WaveguideType.Euler => BuildArc(sx, sy, startAngle, arrivalAngle, ex, ey),
            WaveguideType.SBend => BuildSine(sx, sy, startAngle, ex, ey),
            WaveguideType.Cobra => BuildCobra(sx, sy, startAngle, arrivalAngle, ex, ey),
            _ => BuildStraight(sx, sy, startAngle, ex, ey),
        };
    }

    /// <summary>
    /// Straight: an exact pin-to-pin straight when the end pin lies ahead and (nearly) on the
    /// start pin's axis. Offset pins CANNOT be joined by one straight, so the route falls
    /// back to the connected arc-S.
    /// </summary>
    private static RoutedPath BuildStraight(double sx, double sy, double startAngle, double ex, double ey)
    {
        var (longitudinal, lateral) = LocalFrame(sx, sy, ex, ey, startAngle);
        if (longitudinal > Epsilon && Math.Abs(lateral) < StraightAlignmentToleranceMicrometers)
        {
            var path = new RoutedPath();
            double directAngle = Math.Atan2(ey - sy, ex - sx) * 180.0 / Math.PI;
            path.Segments.Add(new StraightSegment(sx, sy, ex, ey, directAngle));
            return path;
        }

        return BuildArcS(sx, sy, startAngle, ex, ey);
    }

    /// <summary>SBend: the sine curve polyline; degenerate layouts (end pin behind the start)
    /// fall back to the arc-S / direct straight so the pins always stay connected.</summary>
    private static RoutedPath BuildSine(double sx, double sy, double startAngle, double ex, double ey)
    {
        var (longitudinal, lateral) = LocalFrame(sx, sy, ex, ey, startAngle);
        var segments = SineBendGeometry.Build(sx, sy, startAngle, longitudinal, lateral);
        return segments != null ? ToPath(segments) : BuildArcS(sx, sy, startAngle, ex, ey);
    }

    /// <summary>Cobra: the Hermite polyline honoring both end angles; coincident pins fall
    /// back to the arc-S / direct straight.</summary>
    private static RoutedPath BuildCobra(
        double sx, double sy, double startAngle, double arrivalAngle, double ex, double ey)
    {
        var segments = CobraGeometry.Build(sx, sy, startAngle, ex, ey, arrivalAngle);
        return segments != null ? ToPath(segments) : BuildArcS(sx, sy, startAngle, ex, ey);
    }

    /// <summary>
    /// Builds the Bend/Euler route as stub–arc–stub so it reaches BOTH pins exactly.
    /// Degenerate layouts (parallel axes, corner behind a pin) fall back to the two-arc S
    /// via <see cref="BuildArcS"/> — never a silent diagonal.
    /// </summary>
    private static RoutedPath BuildArc(double sx, double sy, double startAngle,
                                       double arrivalAngle, double ex, double ey)
    {
        double sweep = NormalizeSigned(arrivalAngle - startAngle);
        if (Math.Abs(sweep) >= MinArcSweepDegrees && Math.Abs(sweep) <= MaxArcSweepDegrees)
        {
            var arcPath = TryBuildStubArcStub(sx, sy, startAngle, sweep, ex, ey);
            if (arcPath != null)
                return arcPath;
        }

        // Parallel pins (sweep ≈ 0 / 180°) or a corner not ahead of both pins: a single arc
        // cannot join the pins, so fall back to the symmetric S-bend (or a collinear straight).
        return BuildArcS(sx, sy, startAngle, ex, ey);
    }

    /// <summary>
    /// Inscribes a circular arc at the corner C where the start pin's forward axis meets the
    /// end pin's backward axis: solving P1 + t·u1 = C = P2 − s·u2 gives the leg lengths t and s
    /// (both must be positive, i.e. C lies AHEAD of both pins). The arc uses the GENEROUS
    /// radius <see cref="SBendGeometry.GenerousRadiusFactor"/> × min(t, s) / tan(|sweep|/2) —
    /// the largest radius whose tangent length τ = r·tan(|sweep|/2) fits both legs, scaled
    /// slightly down so straight stubs remain on both sides and the radius handles can grab
    /// the arc. The route is stub – arc – stub and hits both pins exactly.
    /// Returns null when the layout is degenerate.
    /// </summary>
    private static RoutedPath? TryBuildStubArcStub(
        double sx, double sy, double startAngle, double sweep, double ex, double ey)
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
        double radius = Math.Min(t, s) * SBendGeometry.GenerousRadiusFactor / tanHalfSweep;
        if (radius <= Epsilon)
            return null;
        double tangent = radius * tanHalfSweep;

        double cornerX = sx + t * u1.X;
        double cornerY = sy + t * u1.Y;
        double arcStartX = cornerX - tangent * u1.X;
        double arcStartY = cornerY - tangent * u1.Y;

        var bend = new BendBuilder(radius).BuildBend(
            arcStartX, arcStartY, startAngle, startAngle + sweep, BendMode.Flexible, radius);
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

    /// <summary>
    /// The two-arc S with the generous radius (<see cref="SBendGeometry"/>), shared by the
    /// Bend/Euler parallel-pin case, the Straight offset fallback and the degenerate cases of
    /// the polyline styles. When even the S is impossible (end pin behind the start), a direct
    /// straight still visibly connects the pins — never a free-floating stub.
    /// </summary>
    private static RoutedPath BuildArcS(double sx, double sy, double startAngle, double ex, double ey)
    {
        var (longitudinal, lateral) = LocalFrame(sx, sy, ex, ey, startAngle);
        var sBend = SBendGeometry.BuildSymmetricS(sx, sy, startAngle, longitudinal, lateral);
        if (sBend != null)
            return ToPath(sBend);

        var path = new RoutedPath();
        double directAngle = Math.Atan2(ey - sy, ex - sx) * 180.0 / Math.PI;
        path.Segments.Add(new StraightSegment(sx, sy, ex, ey, directAngle));
        return path;
    }

    private static RoutedPath ToPath(IReadOnlyList<PathSegment> segments)
    {
        var path = new RoutedPath();
        foreach (var segment in segments)
            path.Segments.Add(segment);
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

    /// <summary>Normalizes an angle in degrees to the range (-180, 180].</summary>
    private static double NormalizeSigned(double angleDegrees)
    {
        double a = angleDegrees % 360.0;
        if (a > 180.0) a -= 360.0;
        if (a <= -180.0) a += 360.0;
        return a;
    }
}
