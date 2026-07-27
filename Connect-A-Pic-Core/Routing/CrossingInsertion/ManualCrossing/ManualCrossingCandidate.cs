using CAP_Core.Components.Connections;

namespace CAP_Core.Routing.CrossingInsertion.ManualCrossing;

/// <summary>
/// A clickable Cut-tool candidate (issue #798): the point where a pin guide
/// line crosses a perpendicular straight waveguide segment with enough straight
/// run on both sides to dock a crossing component's ports.
/// </summary>
/// <param name="Connection">The waveguide connection that would be split.</param>
/// <param name="Segment">The straight segment the guide line intersects.</param>
/// <param name="GuideLine">The pin guide line producing this candidate.</param>
/// <param name="IntersectionPoint">Intersection point in micrometers (crossing center).</param>
/// <param name="SegmentIsHorizontal">True when the intersected segment runs along the X axis.</param>
/// <param name="SegmentDirection">Unit travel direction of the segment (start → end).</param>
public sealed record ManualCrossingCandidate(
    WaveguideConnection Connection,
    StraightSegment Segment,
    PinGuideLine GuideLine,
    (double X, double Y) IntersectionPoint,
    bool SegmentIsHorizontal,
    (double X, double Y) SegmentDirection);
