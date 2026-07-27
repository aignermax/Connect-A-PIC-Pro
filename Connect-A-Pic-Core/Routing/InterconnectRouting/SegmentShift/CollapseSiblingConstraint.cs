namespace CAP_Core.Routing.InterconnectRouting.SegmentShift;

/// <summary>
/// What a pin-lead collapse trial must not make worse for ONE nearby sibling route, captured
/// from the pre-collapse geometry. Three cases:
/// a normal sibling keeps at least its pre-collapse clearance; a sibling that already touches
/// or crosses the route (blocked fallbacks do by construction) tolerates the existing contact,
/// but the trial must not raise the number of crossings with it and must not introduce a
/// collinear overlap; a degenerate sibling (too short to sample as a polyline) is measured
/// point-to-polyline and must keep at least the waveguide spacing — its inflated bounding box
/// or unmeasurable polyline distance must never veto an otherwise clear collapse.
/// </summary>
public sealed class CollapseSiblingConstraint
{
    /// <summary>Total path length (µm) below which a route samples to fewer than two polyline
    /// points, making polyline-to-polyline distance meaningless.</summary>
    private const double DegenerateLengthMicrometers = 0.1;

    private enum Kind { Clearance, Touching, DegeneratePoint }

    private readonly Kind _kind;
    private readonly RoutedPath _sibling;
    private readonly double _requiredClearance;
    private readonly int _maxCrossings;
    private readonly bool _hadCollinearOverlap;
    private readonly (double X, double Y) _point;
    private readonly double _tolerance;

    private CollapseSiblingConstraint(Kind kind, RoutedPath sibling, double tolerance,
        double requiredClearance = 0, int maxCrossings = 0, bool hadCollinearOverlap = false,
        (double X, double Y) point = default)
    {
        _kind = kind;
        _sibling = sibling;
        _tolerance = tolerance;
        _requiredClearance = requiredClearance;
        _maxCrossings = maxCrossings;
        _hadCollinearOverlap = hadCollinearOverlap;
        _point = point;
    }

    /// <summary>
    /// Captures the constraint for one sibling from the route's pre-collapse geometry.
    /// </summary>
    /// <param name="original">The route about to be collapsed (pre-collapse geometry).</param>
    /// <param name="sibling">The nearby sibling route.</param>
    /// <param name="minWaveguideSpacing">The design's minimum waveguide spacing (µm).</param>
    /// <param name="tolerance">Distance (µm) below which two routes count as touching.</param>
    public static CollapseSiblingConstraint For(
        RoutedPath original, RoutedPath sibling, double minWaveguideSpacing, double tolerance)
    {
        if (sibling.TotalLengthMicrometers < DegenerateLengthMicrometers)
        {
            (double X, double Y) point =
                sibling.Segments.Count > 0 ? sibling.Segments[0].StartPoint : (0.0, 0.0);
            double before = PathIntersectionDetector.DistanceToPoint(original, point.X, point.Y);
            // A pre-existing spacing violation is not the collapse's fault: the trial only
            // has to keep the smaller of the spacing and the status quo.
            return new CollapseSiblingConstraint(Kind.DegeneratePoint, sibling, tolerance,
                requiredClearance: Math.Min(before, minWaveguideSpacing), point: point);
        }

        double distance = PathIntersectionDetector.MinimumDistance(original, sibling);
        if (distance < tolerance)
        {
            return new CollapseSiblingConstraint(Kind.Touching, sibling, tolerance,
                maxCrossings: PathIntersectionDetector.CrossingCount(original, sibling),
                hadCollinearOverlap: PathIntersectionDetector.HaveCollinearOverlap(original, sibling));
        }

        return new CollapseSiblingConstraint(Kind.Clearance, sibling, tolerance,
            requiredClearance: distance);
    }

    /// <summary>True when the trial geometry does not worsen this sibling's situation.</summary>
    /// <param name="trial">The candidate collapsed geometry.</param>
    public bool IsSatisfiedBy(RoutedPath trial) => _kind switch
    {
        Kind.DegeneratePoint =>
            PathIntersectionDetector.DistanceToPoint(trial, _point.X, _point.Y)
                >= _requiredClearance - _tolerance,
        Kind.Touching =>
            PathIntersectionDetector.CrossingCount(trial, _sibling) <= _maxCrossings
            && (_hadCollinearOverlap || !PathIntersectionDetector.HaveCollinearOverlap(trial, _sibling)),
        _ =>
            PathIntersectionDetector.MinimumDistance(trial, _sibling)
                >= _requiredClearance - _tolerance,
    };
}
