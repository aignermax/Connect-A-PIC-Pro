using CAP_Core.Routing.AStarPathfinder;

namespace CAP_Core.Routing.InterconnectRouting;

/// <summary>
/// Builds a symmetric arc S-bend that connects two parallel pins offset laterally from one
/// another: it starts at the start pin heading, shifts sideways by the requested lateral
/// offset and arrives PARALLEL to the start heading. Used by the Bend/Euler styles for
/// parallel-offset pins and as the connected fallback of the Straight style.
///
/// Layout is <c>stub – arc – straight – arc – stub</c>: two arcs of a single radius sweep by
/// equal, opposite angles φ, joined by a middle straight, and framed by two short entry/exit
/// straight stubs. The stubs make the arcs INTERIOR bends (flanked by straights on both sides),
/// which is what lets the in-canvas radius handles grab them (<see cref="BendRadiusEditor"/>).
///
/// The radius is GENEROUS by design: <see cref="GenerousRadiusFactor"/> × the largest radius
/// that still fits the offset. The factor &lt; 1 leaves a small middle straight, keeping both
/// arcs interior so the radius handles keep working — a pure two-arc S (factor 1) would put
/// the arcs back-to-back and lose the handles. φ and the middle straight are solved
/// analytically for the inner span (total minus the two stubs).
/// </summary>
public static class SBendGeometry
{
    private const double DegreesToRadians = Math.PI / 180.0;
    private const double Epsilon = 1e-6;
    private const double MinArcSweepDegrees = 2.0;

    /// <summary>Fraction of the forward reach reserved for each entry/exit straight stub.</summary>
    private const double StubFraction = 0.2;

    /// <summary>Fraction of the maximum fitting radius the arcs actually use. Below 1 so a
    /// middle straight remains between the two arcs (see class remarks).</summary>
    public const double GenerousRadiusFactor = 0.9;

    /// <summary>
    /// Builds the S-bend segments in app-space, or returns null when no meaningful S can be
    /// formed (end pin not ahead of the start, negligible lateral offset, or a sweep too small
    /// to be an arc). The caller falls back to a direct straight in those degenerate cases.
    /// </summary>
    /// <param name="startX">Start pin X in app-space micrometers.</param>
    /// <param name="startY">Start pin Y in app-space micrometers.</param>
    /// <param name="startAngleDegrees">Start pin heading in degrees.</param>
    /// <param name="longitudinal">Forward reach along the start heading (µm). Must be positive.</param>
    /// <param name="lateral">Signed lateral offset perpendicular to the start heading (µm).
    /// Its sign selects the turn direction.</param>
    /// <returns>Stub–arc–straight–arc–stub segments, or null in a degenerate case.</returns>
    public static IReadOnlyList<PathSegment>? BuildSymmetricS(
        double startX, double startY, double startAngleDegrees,
        double longitudinal, double lateral)
    {
        if (longitudinal <= Epsilon || Math.Abs(lateral) <= Epsilon)
            return null;

        double stub = longitudinal * StubFraction;
        double innerLongitudinal = longitudinal - 2.0 * stub;
        if (innerLongitudinal <= Epsilon)
            return null;

        double height = Math.Abs(lateral);
        double phi0 = 2.0 * Math.Atan2(height, innerLongitudinal);
        double sinPhi0 = Math.Sin(phi0);
        if (sinPhi0 <= Epsilon)
            return null;

        // Largest radius that still fits without a negative middle straight (pure two-arc S);
        // the generous factor keeps a small middle straight so the arcs stay handle-grabbable.
        double maxRadius = innerLongitudinal / (2.0 * sinPhi0);
        double radius = maxRadius * GenerousRadiusFactor;
        if (radius <= Epsilon)
            return null;

        double phi = SolveHalfSweep(radius, height, innerLongitudinal, phi0);
        double sweepDegrees = phi / DegreesToRadians;
        if (sweepDegrees < MinArcSweepDegrees)
            return null;

        double middleStraight = (innerLongitudinal - 2.0 * radius * Math.Sin(phi)) / Math.Cos(phi);
        if (middleStraight < 0)
            middleStraight = 0;

        return Assemble(startX, startY, startAngleDegrees,
                        sweepDegrees * Math.Sign(lateral), middleStraight, stub, radius);
    }

    /// <summary>
    /// Solves <c>f(φ) = (2R − H)·cos φ + Dx·sin φ − 2R = 0</c> for the half-sweep φ in
    /// <c>(0, φ0]</c> by bisection. <c>f(0) = −H &lt; 0</c> and <c>f(φ0) ≥ 0</c> for
    /// <c>R ≤ maxRadius</c>, so a single root exists in the bracket.
    /// </summary>
    private static double SolveHalfSweep(double radius, double height, double longitudinal, double phiMax)
    {
        double low = 0.0;
        double high = phiMax;
        for (int i = 0; i < 80; i++)
        {
            double mid = (low + high) / 2.0;
            double f = (2.0 * radius - height) * Math.Cos(mid) + longitudinal * Math.Sin(mid) - 2.0 * radius;
            if (f > 0)
                high = mid;
            else
                low = mid;
        }
        return (low + high) / 2.0;
    }

    /// <summary>Builds stub – arc – straight – arc – stub forward from the start pin.</summary>
    private static IReadOnlyList<PathSegment>? Assemble(
        double startX, double startY, double startAngleDegrees,
        double signedSweepDegrees, double middleStraight, double stub, double radius)
    {
        var bendBuilder = new BendBuilder(radius);
        var segments = new List<PathSegment>();
        double x = startX, y = startY, angle = startAngleDegrees;

        (x, y) = AppendStraight(segments, x, y, angle, stub);

        var arc1 = bendBuilder.BuildBend(x, y, angle, angle + signedSweepDegrees, BendMode.Flexible, radius);
        if (arc1 == null)
            return null;
        segments.Add(arc1);
        (x, y, angle) = (arc1.EndPoint.X, arc1.EndPoint.Y, arc1.EndAngleDegrees);

        (x, y) = AppendStraight(segments, x, y, angle, middleStraight);

        var arc2 = bendBuilder.BuildBend(x, y, angle, startAngleDegrees, BendMode.Flexible, radius);
        if (arc2 == null)
            return null;
        segments.Add(arc2);
        (x, y, angle) = (arc2.EndPoint.X, arc2.EndPoint.Y, arc2.EndAngleDegrees);

        AppendStraight(segments, x, y, angle, stub);
        return segments;
    }

    /// <summary>Adds a straight of <paramref name="length"/> along <paramref name="angleDegrees"/>
    /// (skipped when negligible) and returns the new position.</summary>
    private static (double X, double Y) AppendStraight(
        List<PathSegment> segments, double x, double y, double angleDegrees, double length)
    {
        if (length <= Epsilon)
            return (x, y);
        double rad = angleDegrees * DegreesToRadians;
        double endX = x + length * Math.Cos(rad);
        double endY = y + length * Math.Sin(rad);
        segments.Add(new StraightSegment(x, y, endX, endY, angleDegrees));
        return (endX, endY);
    }
}
