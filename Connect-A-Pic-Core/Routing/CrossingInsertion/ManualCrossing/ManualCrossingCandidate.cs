using CAP_Core.Components.Connections;

namespace CAP_Core.Routing.CrossingInsertion.ManualCrossing;

/// <summary>
/// A clickable Cut-tool candidate: either the point where a pin guide line crosses a
/// perpendicular straight waveguide segment, or — when no such guide intersection is within
/// snap range of the pointer — a free-cut point projected directly onto a cuttable segment.
/// Either way there is enough straight run on both sides to dock a crossing component's ports.
/// </summary>
/// <param name="Connection">The waveguide connection that would be split.</param>
/// <param name="Segment">The straight segment the candidate sits on.</param>
/// <param name="GuideLine">
/// The pin guide line producing this candidate, or null for a free cut (no guide involved —
/// the candidate is simply the pointer position projected onto the segment).
/// </param>
/// <param name="IntersectionPoint">Candidate point in micrometers (crossing center).</param>
/// <param name="SegmentIsHorizontal">True when the segment runs along the X axis.</param>
/// <param name="SegmentDirection">Unit travel direction of the segment (start → end).</param>
public sealed record ManualCrossingCandidate(
    WaveguideConnection Connection,
    StraightSegment Segment,
    PinGuideLine? GuideLine,
    (double X, double Y) IntersectionPoint,
    bool SegmentIsHorizontal,
    (double X, double Y) SegmentDirection)
{
    /// <summary>
    /// True when this candidate has no guide line: the pointer was not within snap range of
    /// any guide intersection, so the Cut tool falls back to cutting directly at the
    /// projected point on the nearest eligible segment.
    /// </summary>
    public bool IsFreeCut => GuideLine is null;
}
