using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Routing.AStarPathfinder;

namespace CAP_Core.Routing.InterconnectRouting;

/// <summary>
/// Builds the DIRECT styled candidate the router tries BEFORE falling back to A*
/// (issue #860): for a clear line between two pins a smooth straight / arc-S / sine /
/// cobra is almost always the route a photonics designer expects, while grid-based A*
/// produces Manhattan-style detours that add bend loss and wall off later routes.
///
/// The style is chosen from the pin geometry, mirroring the explicit-style rules of
/// <see cref="ConnectionStyleRouteBuilder"/>:
/// <list type="bullet">
/// <item>Arc geometry first (<see cref="WaveguideType.Bend"/>): exact straight for
/// collinear facing pins, two-arc S for parallel-offset pins, stub–arc–stub for angled
/// pins — all honoring the bend-radius floor when it fits.</item>
/// <item>When the arcs cannot honor the floor: the smooth polyline — sine S-bend for
/// parallel pins, cobra for angled pins — accepted only when its sampled curvature stays
/// above the floor.</item>
/// </list>
///
/// A candidate is only a PROPOSAL: the router verifies it against the component obstacle
/// grid A* uses and against the exact geometry of registered sibling routes, and falls
/// back to A* when the styled path is actually blocked. Returns null
/// when no styled geometry can leave the start pin along its direction (e.g. the end pin
/// lies behind the start) or none satisfies the radius floor — A* then routes as before.
/// </summary>
public static class DirectRouteFirstPolicy
{
    /// <summary>Bend radii this close to the floor (µm) still count as satisfying it.</summary>
    private const double RadiusToleranceMicrometers = 1e-3;

    /// <summary>Polyline vertex turns below this (radians) are treated as straight —
    /// their osculating radius is effectively infinite and numerically unstable.</summary>
    private const double MinVertexTurnRadians = 1e-4;

    /// <summary>Sampled polyline curvature may undercut the floor by this factor: the
    /// chord construction slightly underestimates the true osculating radius.</summary>
    private const double ChordApproximationFactor = 0.95;

    /// <summary>Two pin axes within this |turn| (degrees) count as parallel; the smooth
    /// polyline for them is the sine S-bend, otherwise the angle-matching cobra.</summary>
    private const double ParallelToleranceDegrees = 1.0;

    /// <summary>
    /// Builds the direct styled candidate between two pins, or null when no styled
    /// geometry fits the layout at the given bend-radius floor.
    /// </summary>
    /// <param name="startPin">Source pin; the candidate leaves it along its direction.</param>
    /// <param name="endPin">Target pin; the candidate arrives at its input angle.</param>
    /// <param name="minBendRadiusMicrometers">Bend-radius floor (µm) — typically the larger
    /// of the connection radius and the fabrication process minimum. 0 applies no floor.</param>
    /// <returns>The candidate path (unverified against obstacles), or null.</returns>
    public static RoutedPath? TryBuildCandidate(
        PhysicalPin startPin, PhysicalPin endPin, double minBendRadiusMicrometers) =>
        TryBuildWithStyle(startPin, endPin, minBendRadiusMicrometers, out _);

    /// <summary>Like <see cref="TryBuildCandidate"/>, also reporting the chosen style.</summary>
    public static RoutedPath? TryBuildWithStyle(
        PhysicalPin startPin, PhysicalPin endPin, double minBendRadiusMicrometers,
        out CAP_Core.Components.Connections.WaveguideType style)
    {
        var arcPath = ConnectionStyleRouteBuilder.Build(
            startPin, endPin, WaveguideType.Bend, minBendRadiusMicrometers);
        if (MeetsRadiusFloor(arcPath, minBendRadiusMicrometers))
        {
            style = WaveguideType.Bend;
            return arcPath;
        }

        var smoothStyle = PinAxesAreParallel(startPin, endPin)
            ? WaveguideType.SBend
            : WaveguideType.Cobra;
        var smoothPath = ConnectionStyleRouteBuilder.Build(
            startPin, endPin, smoothStyle, minBendRadiusMicrometers);
        if (MeetsRadiusFloor(smoothPath, minBendRadiusMicrometers))
        {
            style = smoothStyle;
            return smoothPath;
        }
        style = WaveguideType.Auto;
        return null;
    }

    /// <summary>True when the start heading and the end pin's arrival heading are parallel
    /// (same direction of travel), so a lateral S-shift joins the pins.</summary>
    private static bool PinAxesAreParallel(PhysicalPin startPin, PhysicalPin endPin)
    {
        double arrivalAngle = endPin.GetAbsoluteAngle() + 180.0;
        double turn = AngleUtilities.NormalizeAngle(arrivalAngle - startPin.GetAbsoluteAngle());
        return Math.Abs(turn) <= ParallelToleranceDegrees;
    }

    /// <summary>
    /// True when every bend of the path satisfies the radius floor: arc segments by their
    /// explicit radius, polyline chords by the sampled (circumscribed-circle) radius at
    /// each interior vertex. Conservative by design — a violating candidate is rejected so
    /// the radius-aware A* routes instead.
    /// </summary>
    private static bool MeetsRadiusFloor(RoutedPath? path, double minBendRadiusMicrometers)
    {
        if (path == null)
            return false;
        if (minBendRadiusMicrometers <= 0)
            return true;

        foreach (var bend in path.Segments.OfType<BendSegment>())
        {
            if (bend.RadiusMicrometers < minBendRadiusMicrometers - RadiusToleranceMicrometers)
                return false;
        }
        return PolylineMeetsRadiusFloor(path, minBendRadiusMicrometers);
    }

    /// <summary>Checks the sampled curvature radius at every vertex joining two straight
    /// chords (sine/cobra polylines) against the floor.</summary>
    private static bool PolylineMeetsRadiusFloor(RoutedPath path, double minBendRadiusMicrometers)
    {
        for (int i = 1; i < path.Segments.Count; i++)
        {
            if (path.Segments[i - 1] is not StraightSegment previous ||
                path.Segments[i] is not StraightSegment current)
                continue;

            double turnRadians = Math.Abs(SignedTurnDegrees(
                previous.StartAngleDegrees, current.StartAngleDegrees)) * Math.PI / 180.0;
            if (turnRadians < MinVertexTurnRadians)
                continue;

            double meanChord = (previous.LengthMicrometers + current.LengthMicrometers) / 2.0;
            double sampledRadius = meanChord / (2.0 * Math.Sin(turnRadians / 2.0));
            if (sampledRadius < minBendRadiusMicrometers * ChordApproximationFactor)
                return false;
        }
        return true;
    }

    /// <summary>Difference between two headings normalized to (-180, 180] degrees.</summary>
    private static double SignedTurnDegrees(double fromDegrees, double toDegrees)
    {
        double turn = (toDegrees - fromDegrees) % 360.0;
        if (turn > 180.0) turn -= 360.0;
        if (turn <= -180.0) turn += 360.0;
        return turn;
    }
}
