namespace CAP_Core.Routing.InterconnectRouting.SegmentShift;

/// <summary>
/// Pure geometry for the segment parallel-shift handles (issue #791): enumerates which
/// straight segments of a routed path are shiftable and maps pointer positions onto the
/// perpendicular shift axis. Mutation lives in <see cref="SegmentShiftEditor"/>.
/// </summary>
public static class SegmentShiftGeometry
{
    private const double DegreesToRadians = Math.PI / 180.0;
    private const double Epsilon = 1e-9;

    /// <summary>
    /// Returns a midpoint handle for every <b>shiftable</b> straight segment of the path:
    /// a straight flanked by a bend on each side whose outer neighbours are straights again
    /// (pattern straight–bend–straight–bend–straight), so both bends can absorb the shift by
    /// sliding along the outer straights. Direction works for axis-aligned and 45° diagonal
    /// segments alike — the constraint axis is simply the segment normal.
    /// Non-shiftable straights are skipped but still advance the straight index, so
    /// <see cref="StraightSegmentHandle.StraightIndex"/> matches
    /// <see cref="SegmentShiftEditor.TryApplyShift"/>.
    /// </summary>
    /// <param name="segments">The connection's routed path segments.</param>
    /// <returns>One handle per shiftable straight segment, in path order.</returns>
    public static IReadOnlyList<StraightSegmentHandle> GetHandles(IReadOnlyList<PathSegment> segments)
    {
        var handles = new List<StraightSegmentHandle>();
        if (segments == null || segments.Count == 0)
            return handles;

        int straightIndex = -1;
        for (int i = 0; i < segments.Count; i++)
        {
            if (segments[i] is not StraightSegment straight)
                continue;
            straightIndex++;

            if (!IsShiftable(segments, i))
                continue;
            handles.Add(BuildHandle(straight, straightIndex));
        }
        return handles;
    }

    /// <summary>
    /// True when the straight segment at <paramref name="segmentIndex"/> can be shifted:
    /// it must sit between two bends that each have another straight on their outer side,
    /// and neither adjacent bend may leave it parallel to its outer straight.
    /// Mirrors the guards in <see cref="SegmentShiftEditor.TryApplyShift"/>.
    /// </summary>
    public static bool IsShiftable(IReadOnlyList<PathSegment> segments, int segmentIndex)
    {
        if (segmentIndex < 2 || segmentIndex > segments.Count - 3)
            return false;
        if (segments[segmentIndex] is not StraightSegment straight)
            return false;
        if (segments[segmentIndex - 1] is not BendSegment || segments[segmentIndex + 1] is not BendSegment)
            return false;
        if (segments[segmentIndex - 2] is not StraightSegment before ||
            segments[segmentIndex + 2] is not StraightSegment after)
            return false;

        var normal = NormalOf(straight);
        return Math.Abs(Dot(UnitVector(before.StartAngleDegrees), normal)) > Epsilon
            && Math.Abs(Dot(UnitVector(after.StartAngleDegrees), normal)) > Epsilon;
    }

    /// <summary>
    /// Signed shift offset (µm) of <paramref name="pointer"/> relative to the handle's
    /// midpoint, projected onto the segment normal — the drag is constrained to this axis,
    /// movement along the segment is ignored.
    /// </summary>
    /// <param name="handle">The handle captured when the drag started.</param>
    /// <param name="pointer">Current pointer position in canvas micrometers.</param>
    public static double ProjectOffset(StraightSegmentHandle handle, (double X, double Y) pointer)
        => Dot((pointer.X - handle.Midpoint.X, pointer.Y - handle.Midpoint.Y), handle.Normal);

    /// <summary>Unit direction of a straight segment, taken from its path angle so it stays
    /// defined even when the segment is momentarily collapsed to zero length.</summary>
    public static (double X, double Y) DirectionOf(StraightSegment straight)
        => UnitVector(straight.StartAngleDegrees);

    /// <summary>Unit normal of a straight segment (direction rotated +90°).</summary>
    public static (double X, double Y) NormalOf(StraightSegment straight)
    {
        var (dx, dy) = DirectionOf(straight);
        return (-dy, dx);
    }

    /// <summary>Dot product of two 2D vectors.</summary>
    public static double Dot((double X, double Y) a, (double X, double Y) b)
        => a.X * b.X + a.Y * b.Y;

    /// <summary>Unit vector for a path angle in degrees.</summary>
    public static (double X, double Y) UnitVector(double angleDegrees)
    {
        double rad = angleDegrees * DegreesToRadians;
        return (Math.Cos(rad), Math.Sin(rad));
    }

    private static StraightSegmentHandle BuildHandle(StraightSegment straight, int straightIndex)
    {
        var midpoint = ((straight.StartPoint.X + straight.EndPoint.X) / 2.0,
                        (straight.StartPoint.Y + straight.EndPoint.Y) / 2.0);
        return new StraightSegmentHandle(straightIndex, midpoint, DirectionOf(straight), NormalOf(straight));
    }
}
