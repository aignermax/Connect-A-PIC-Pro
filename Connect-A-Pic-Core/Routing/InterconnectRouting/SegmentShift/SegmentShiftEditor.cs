using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;

namespace CAP_Core.Routing.InterconnectRouting.SegmentShift;

/// <summary>
/// Applies manual parallel shifts to straight segments of a routed waveguide path
/// (issue #791). A shift translates the straight along its normal; the two adjoining
/// bends slide rigidly along their outer straight segments so the route stays connected
/// and tangent (bend radii and sweeps are untouched). Shifts that would collapse or invert
/// a segment are rejected — honest clamp, no silent geometry corruption. Applying a shift
/// freezes the route, mirroring <see cref="BendRadiusEditor"/>.
/// </summary>
public static class SegmentShiftEditor
{
    private const double Epsilon = 1e-9;
    private const double LengthToleranceMicrometers = 1e-6;

    /// <summary>
    /// Tries to set the cumulative perpendicular shift of the straight segment at
    /// <paramref name="straightIndex"/> (index among the path's straight segments, 0-based)
    /// to <paramref name="offsetMicrometers"/>, measured along the segment normal from the
    /// auto-routed position. Only the delta to the currently recorded shift is applied, so
    /// repeated calls with the same offset are no-ops — undo/redo re-apply safely.
    /// On success the offset is recorded, the route is frozen and losses are refreshed.
    /// </summary>
    /// <param name="connection">The connection whose routed path is edited.</param>
    /// <param name="straightIndex">0-based index of the straight segment along the path.</param>
    /// <param name="offsetMicrometers">Desired cumulative shift in micrometers (positive along
    /// the segment normal, see <see cref="StraightSegmentHandle.Normal"/>).</param>
    /// <param name="error">Human-readable reason when the edit is not possible.</param>
    /// <returns>True when the segment geometry matches the requested offset.</returns>
    public static bool TryApplyShift(WaveguideConnection connection, int straightIndex,
                                     double offsetMicrometers, out string? error)
    {
        error = null;
        var segments = connection.RoutedPath?.Segments;
        if (segments == null || segments.Count == 0)
        {
            error = "Connection has no routed path.";
            return false;
        }

        int segmentIndex = FindStraightSegmentIndex(segments, straightIndex);
        if (segmentIndex < 0)
        {
            error = $"Straight segment #{straightIndex + 1} not found.";
            return false;
        }
        if (!SegmentShiftGeometry.IsShiftable(segments, segmentIndex))
        {
            error = "Only straight segments between two bends flanked by straights can be shifted.";
            return false;
        }

        double currentOffset = connection.StraightShiftOffsets.TryGetValue(straightIndex, out double stored)
            ? stored : 0.0;
        double delta = offsetMicrometers - currentOffset;
        if (Math.Abs(delta) < Epsilon)
            return true;

        if (!TryShiftStraightSegment(segments, segmentIndex, delta, out error))
            return false;

        connection.StraightShiftOffsets[straightIndex] = offsetMicrometers;
        connection.IsRouteFrozen = true;
        connection.UpdateLossFromPath();
        return true;
    }

    /// <summary>
    /// Re-runs the router's component collision check on the connection's current path and
    /// records the result on <see cref="RoutedPath.PassesThroughComponent"/>, so a shift into
    /// an obstacle is flagged through the existing design-issue pipeline instead of silently
    /// producing an invalid route. Call after a drop and after undo/redo.
    /// The route may hug its own pins: cells inside the pin corridors of the connection's
    /// endpoint pins that only a NEIGHBOUR's padding band covers are tolerated — a foreign
    /// component placed next to a pin must not raise a false collision flag on a collapsed
    /// route. A foreign component body inside the corridor still flags.
    /// </summary>
    /// <param name="connection">The connection whose path was edited.</param>
    /// <param name="router">The router owning the pathfinding grid.</param>
    public static void RefreshComponentCollision(WaveguideConnection connection, WaveguideRouter router)
    {
        if (connection.RoutedPath == null)
            return;
        connection.RoutedPath.PassesThroughComponent =
            router.IsPathBlockedByComponents(connection.RoutedPath.Segments, OwnEndpointPins(connection));
    }

    /// <summary>The connection's endpoint pins, whose pin corridors the route may hug.</summary>
    private static List<PhysicalPin> OwnEndpointPins(WaveguideConnection connection)
    {
        var pins = new List<PhysicalPin>(2);
        if (connection.StartPin != null) pins.Add(connection.StartPin);
        if (connection.EndPin != null) pins.Add(connection.EndPin);
        return pins;
    }

    /// <summary>Returns the segment index of the n-th straight segment, or -1 when out of range.</summary>
    private static int FindStraightSegmentIndex(IReadOnlyList<PathSegment> segments, int straightIndex)
    {
        if (straightIndex < 0)
            return -1;
        int seen = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            if (segments[i] is StraightSegment && seen++ == straightIndex)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Translates the straight at <paramref name="segmentIndex"/> by <paramref name="delta"/>
    /// along its normal. Each adjoining bend translates rigidly along its outer straight's
    /// direction by the amount whose perpendicular component equals the shift, so tangency is
    /// preserved by construction. All clamps are validated before anything is mutated — the
    /// honest clamp rejects a shift that would collapse (invert) any of the three straights.
    /// Operates directly on the segment list without touching connection state, so it is
    /// reusable by automated passes (e.g. <see cref="PinStraightCollapser"/>) as well as by
    /// <see cref="TryApplyShift"/>.
    /// </summary>
    /// <param name="segments">The path segments to mutate in place.</param>
    /// <param name="segmentIndex">Index of the shiftable straight (pattern straight–bend–straight
    /// –bend–straight around it).</param>
    /// <param name="delta">Perpendicular shift in micrometers along the straight's normal.</param>
    /// <param name="error">Human-readable reason when the shift is rejected.</param>
    /// <returns>True when the shift was applied; false (segments untouched) when clamped.</returns>
    public static bool TryShiftStraightSegment(IReadOnlyList<PathSegment> segments, int segmentIndex,
                                               double delta, out string? error)
    {
        error = null;
        var straight = (StraightSegment)segments[segmentIndex];
        var bendBefore = (BendSegment)segments[segmentIndex - 1];
        var bendAfter = (BendSegment)segments[segmentIndex + 1];
        var outerBefore = (StraightSegment)segments[segmentIndex - 2];
        var outerAfter = (StraightSegment)segments[segmentIndex + 2];

        var normal = SegmentShiftGeometry.NormalOf(straight);
        var dirBefore = SegmentShiftGeometry.UnitVector(outerBefore.StartAngleDegrees);
        var dirAfter = SegmentShiftGeometry.UnitVector(outerAfter.StartAngleDegrees);

        // The bend before slides along the incoming outer straight; its travel distance is
        // whatever produces a perpendicular displacement of exactly `delta` on the segment.
        double dotBefore = SegmentShiftGeometry.Dot(dirBefore, normal);
        double dotAfter = SegmentShiftGeometry.Dot(dirAfter, normal);
        if (Math.Abs(dotBefore) < Epsilon || Math.Abs(dotAfter) < Epsilon)
        {
            error = "Adjacent segments are parallel to the shifted segment.";
            return false;
        }
        var shiftBefore = Scale(dirBefore, delta / dotBefore);
        var shiftAfter = Scale(dirAfter, delta / dotAfter);

        if (!KeepsForwardLength(outerBefore.StartPoint, Add(outerBefore.EndPoint, shiftBefore), dirBefore))
        {
            error = "Shift too far: the straight segment before the bend would collapse.";
            return false;
        }
        if (!KeepsForwardLength(Add(outerAfter.StartPoint, shiftAfter), outerAfter.EndPoint, dirAfter))
        {
            error = "Shift too far: the straight segment after the bend would collapse.";
            return false;
        }
        if (!KeepsForwardLength(Add(straight.StartPoint, shiftBefore), Add(straight.EndPoint, shiftAfter),
                                SegmentShiftGeometry.DirectionOf(straight)))
        {
            error = "Shift too far: the shifted segment itself would collapse.";
            return false;
        }

        outerBefore.EndPoint = Add(outerBefore.EndPoint, shiftBefore);
        Translate(bendBefore, shiftBefore);
        straight.StartPoint = Add(straight.StartPoint, shiftBefore);
        straight.EndPoint = Add(straight.EndPoint, shiftAfter);
        Translate(bendAfter, shiftAfter);
        outerAfter.StartPoint = Add(outerAfter.StartPoint, shiftAfter);
        return true;
    }

    /// <summary>True when the segment from start to end still points along its path
    /// direction with non-negative length (within tolerance).</summary>
    private static bool KeepsForwardLength((double X, double Y) start, (double X, double Y) end,
                                           (double X, double Y) direction)
        => SegmentShiftGeometry.Dot((end.X - start.X, end.Y - start.Y), direction)
           >= -LengthToleranceMicrometers;

    private static void Translate(BendSegment bend, (double X, double Y) by)
    {
        bend.StartPoint = Add(bend.StartPoint, by);
        bend.EndPoint = Add(bend.EndPoint, by);
        bend.Center = Add(bend.Center, by);
    }

    private static (double X, double Y) Add((double X, double Y) a, (double X, double Y) b)
        => (a.X + b.X, a.Y + b.Y);

    private static (double X, double Y) Scale((double X, double Y) v, double factor)
        => (v.X * factor, v.Y * factor);
}
