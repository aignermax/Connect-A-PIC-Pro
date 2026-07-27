namespace CAP_Core.Routing.InterconnectRouting.SegmentShift;

/// <summary>
/// Post-smoothing pass that collapses the pin-side straight leads of a freshly auto-routed path
/// toward their pins, so the first and last bends sit as close to the pins as the geometry and
/// collision clamp allows (ideally directly at the pin, with no wasted lead-in). It reuses the
/// honest parallel-shift clamp of <see cref="SegmentShiftEditor.TryShiftStraightSegment"/>: a
/// lead is removed by sliding the adjacent shiftable straight, never by inventing new geometry.
/// A shift is kept only when it strictly reduces the combined pin-lead length and passes a
/// caller-supplied acceptance predicate (self-intersection, component and foreign-waveguide
/// collision); otherwise it is fully reverted, so the result is never worse than the smoothed
/// input. Intended only for auto-routed paths; frozen or manually edited routes are the domain
/// of <see cref="SegmentShiftEditor"/> and never reach this pass.
/// </summary>
public static class PinStraightCollapser
{
    private const double Epsilon = 1e-6;

    /// <summary>Minimum segment count for a collapsible lead: straight–bend–straight–bend–straight.</summary>
    private const int MinCollapsibleSegments = 5;

    /// <summary>Segment index of the shiftable straight adjacent to the start-pin lead.</summary>
    private const int StartShiftableIndex = 2;

    /// <summary>
    /// Collapses the first and last straight segments of <paramref name="path"/> toward their
    /// pins, mutating the path in place. <paramref name="isAcceptable"/> returns true when a
    /// trial-shifted path may be kept; any shift that is rejected — or that fails to shorten the
    /// combined pin lead — is fully reverted.
    /// </summary>
    /// <param name="path">The freshly smoothed auto-routed path (mutated in place).</param>
    /// <param name="isAcceptable">Validation of the whole path after a trial shift.</param>
    public static void Collapse(RoutedPath path, Func<RoutedPath, bool> isAcceptable)
    {
        var segments = path.Segments;
        if (segments.Count < MinCollapsibleSegments)
            return;

        if (segments.Count == MinCollapsibleSegments)
        {
            CollapseSharedShiftable(path, isAcceptable);
            return;
        }

        // Longer paths carry an independent shiftable straight next to each pin lead, so both
        // leads can be driven toward their pins without one trading off against the other.
        TryCollapseLead(path, isAcceptable, StartShiftableIndex, leadBeforeShiftable: true);
        TryCollapseLead(path, isAcceptable, segments.Count - 3, leadBeforeShiftable: false);
    }

    /// <summary>
    /// True when the path has a pin-side straight lead adjacent to a shiftable straight, i.e.
    /// something this pass could collapse. A cheap structural pre-check so callers can skip the
    /// expensive sibling-clearance scan for routes that cannot be shortened anyway.
    /// </summary>
    /// <param name="path">The routed path to inspect.</param>
    public static bool HasCollapsibleLead(RoutedPath path)
    {
        var segments = path.Segments;
        if (segments.Count < MinCollapsibleSegments)
            return false;
        return SegmentShiftGeometry.IsShiftable(segments, StartShiftableIndex)
            || SegmentShiftGeometry.IsShiftable(segments, segments.Count - 3);
    }

    /// <summary>
    /// Handles the five-segment path whose single middle straight is shared by both pin leads.
    /// Collapsing the shorter lead is the largest feasible shift; when the outer straights are
    /// antiparallel (a U-turn) it drives the longer lead down too, and the total-lead gate in
    /// <see cref="TryCollapseLead"/> discards the trade-only case of parallel outer straights.
    /// </summary>
    private static void CollapseSharedShiftable(RoutedPath path, Func<RoutedPath, bool> isAcceptable)
    {
        var segments = path.Segments;
        if (segments[0] is not StraightSegment leadBefore || segments[^1] is not StraightSegment leadAfter)
            return;

        bool collapseBefore = leadBefore.LengthMicrometers <= leadAfter.LengthMicrometers;
        TryCollapseLead(path, isAcceptable, StartShiftableIndex, collapseBefore);
    }

    /// <summary>
    /// Shifts the shiftable straight at <paramref name="shiftableIndex"/> by exactly the offset
    /// that drives its neighbouring pin lead to zero length, keeping the change only when the
    /// combined pin lead strictly shrinks and <paramref name="isAcceptable"/> still holds.
    /// </summary>
    /// <param name="leadBeforeShiftable">True to collapse the lead two segments before the
    /// shiftable straight (start pin), false for the lead two segments after it (end pin).</param>
    private static void TryCollapseLead(RoutedPath path, Func<RoutedPath, bool> isAcceptable,
                                        int shiftableIndex, bool leadBeforeShiftable)
    {
        var segments = path.Segments;
        if (!SegmentShiftGeometry.IsShiftable(segments, shiftableIndex))
            return;

        int leadIndex = leadBeforeShiftable ? shiftableIndex - 2 : shiftableIndex + 2;
        double leadLength = ((StraightSegment)segments[leadIndex]).LengthMicrometers;
        if (leadLength < Epsilon)
            return; // bend already sits at the pin

        double delta = LeadCollapseOffset(segments, shiftableIndex, leadIndex, leadBeforeShiftable, leadLength);
        if (double.IsNaN(delta))
            return;

        double totalLeadBefore = TotalPinLead(path);
        var snapshot = path.DeepCopy();
        if (!SegmentShiftEditor.TryShiftStraightSegment(segments, shiftableIndex, delta, out _))
            return; // clamp rejected the shift; segments are untouched, nothing to restore

        if (!isAcceptable(path) || TotalPinLead(path) > totalLeadBefore - Epsilon)
            Restore(path, snapshot);
    }

    /// <summary>
    /// The perpendicular shift (µm) of the shiftable straight that zeroes the pin lead. The lead
    /// rides the shift along its own direction: the before-neighbour moves by its END point
    /// (length changes by +delta/dot), the after-neighbour by its START point (−delta/dot), where
    /// dot is the projection of the lead direction onto the shiftable straight's normal. Returns
    /// NaN when the lead runs parallel to that normal and cannot be driven by the shift.
    /// </summary>
    private static double LeadCollapseOffset(IReadOnlyList<PathSegment> segments, int shiftableIndex,
                                             int leadIndex, bool leadBeforeShiftable, double leadLength)
    {
        var shiftable = (StraightSegment)segments[shiftableIndex];
        var lead = (StraightSegment)segments[leadIndex];
        var normal = SegmentShiftGeometry.NormalOf(shiftable);
        var leadDirection = SegmentShiftGeometry.UnitVector(lead.StartAngleDegrees);
        double dot = SegmentShiftGeometry.Dot(leadDirection, normal);
        if (Math.Abs(dot) < Epsilon)
            return double.NaN;

        return leadBeforeShiftable ? -leadLength * dot : leadLength * dot;
    }

    /// <summary>Combined length of the two pin-side straight leads (0 for a lead that is a bend).</summary>
    private static double TotalPinLead(RoutedPath path)
    {
        var segments = path.Segments;
        double total = 0;
        if (segments[0] is StraightSegment first)
            total += first.LengthMicrometers;
        if (segments[^1] is StraightSegment last)
            total += last.LengthMicrometers;
        return total;
    }

    private static void Restore(RoutedPath path, RoutedPath snapshot)
    {
        path.Segments.Clear();
        path.Segments.AddRange(snapshot.Segments);
    }
}
