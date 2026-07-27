namespace CAP_Core.Routing.InterconnectRouting.SegmentShift;

/// <summary>
/// Describes the in-canvas midpoint handle of a shiftable straight segment (issue #791).
/// The handle sits at the segment's centre; dragging it is constrained to
/// <see cref="Normal"/>, so the segment moves parallel to itself.
/// </summary>
/// <param name="StraightIndex">0-based index of the segment among the path's straight segments,
/// as accepted by <c>SegmentShiftEditor.TryApplyShift</c>.</param>
/// <param name="Midpoint">Centre of the straight segment in canvas micrometers.</param>
/// <param name="Direction">Unit vector along the segment (start → end).</param>
/// <param name="Normal">Unit vector perpendicular to the segment (left of
/// <paramref name="Direction"/>); positive shift offsets move the segment this way.</param>
public sealed record StraightSegmentHandle(
    int StraightIndex,
    (double X, double Y) Midpoint,
    (double X, double Y) Direction,
    (double X, double Y) Normal);
