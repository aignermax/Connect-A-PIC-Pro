using CAP_Core.Routing.AStarPathfinder;

namespace CAP_Core.Routing;

/// <summary>
/// Represents a routed path consisting of multiple segments.
/// </summary>
public class RoutedPath
{
    public List<PathSegment> Segments { get; } = new();

    /// <summary>
    /// Indicates if this path was created as a fallback because no valid path could be found.
    /// When true, the path may pass through obstacles and should be displayed differently.
    /// </summary>
    public bool IsBlockedFallback { get; set; } = false;

    /// <summary>
    /// Indicates if this path has invalid geometry (e.g., segments too short for minimum bend radius).
    /// When true, the path violates physical constraints and should be displayed as an error (red).
    /// </summary>
    public bool IsInvalidGeometry { get; set; } = false;

    /// <summary>
    /// True when these segments are an honest placeholder rather than a real route: the router
    /// gave up on a self-crossing fallback (no optical model) and replaced it with a straight
    /// line between the pins, purely so the connection has SOME geometry to flag as unroutable
    /// (see <see cref="WaveguideRouter"/>'s degrade-to-blocked-fallback step). Distinct from
    /// <see cref="IsBlockedFallback"/>, which also covers two cases that ARE real, exportable
    /// geometry: a fallback that merely grazes an obstacle without looping, and the crossing
    /// diagnostic <c>WaveguideConnectionManager</c> stamps on an unresolved sibling overlap
    /// (including a metal/optical crossing legitimately resolved by a bridge marker). Export
    /// eligibility must key off this flag (and <see cref="IsInvalidGeometry"/>), never off
    /// <see cref="IsBlockedFallback"/> alone.
    /// </summary>
    public bool IsPlaceholderGeometry { get; set; } = false;

    /// <summary>
    /// True when the path could only be routed with a bend radius below the governing
    /// fabrication process' minimum — the endpoint chiplet's per-connection floor
    /// (<see cref="WaveguideRouter.ProcessMinBendRadiusForPinPair"/>) when wired, else the
    /// canvas-wide <see cref="WaveguideRouter.ProcessMinBendRadiusMicrometers"/>.
    /// The geometry itself is clean, but the design violates the process rule; the
    /// design checks surface it as a <c>BendRadiusBelowProcessMinimum</c> issue.
    /// </summary>
    public bool ViolatesProcessMinBendRadius { get; set; } = false;

    /// <summary>
    /// True when a STYLED (forced-shape) route passes through a component obstacle.
    /// Styled routes deliberately ignore obstacles and are never auto-rerouted, so a
    /// collision cannot be resolved by the router; the design checks surface it as a
    /// <c>StyledRouteThroughComponent</c> issue instead. Refreshed on every routing pass.
    /// </summary>
    public bool PassesThroughComponent { get; set; } = false;

    /// <summary>
    /// True when this Auto route was produced by the direct/S-bend-first policy (issue #860):
    /// a smooth styled geometry verified against the obstacle grid, not an A* grid path.
    /// The pin-lead collapse pass skips such routes — their entry/exit stubs are intended
    /// styled geometry, not the forced grid-escape leads the collapse exists to remove.
    /// </summary>
    public bool IsDirectStyledRoute { get; set; } = false;

    /// <summary>
    /// The style a direct-styled route was built with (straight/S-bend/cobra), null for
    /// A*/fallback routes. Display-only: lets the UI name the effective geometry while the
    /// connection's own Type stays Auto.
    /// </summary>
    public CAP_Core.Components.Connections.WaveguideType? DirectStyle { get; set; }

    /// <summary>
    /// Debug information: The raw A* grid path used to generate this path.
    /// Only populated when A* routing is used.
    /// </summary>
    public List<AStarNode>? DebugGridPath { get; set; } = null;

    /// <summary>
    /// Total length of the path in micrometers.
    /// </summary>
    public double TotalLengthMicrometers => Segments.Sum(s => s.LengthMicrometers);

    /// <summary>
    /// Total equivalent 90-degree bends in the path.
    /// </summary>
    public double TotalEquivalent90DegreeBends => Segments
        .OfType<BendSegment>()
        .Sum(b => b.Equivalent90DegreeBends);

    /// <summary>
    /// Creates an independent deep copy (segments cloned, flags copied). Use whenever
    /// stored geometry (e.g. a group's frozen internal paths kept for Undo) is handed
    /// to a live connection: sharing the segment objects would let canvas edits
    /// (bend-radius handles mutate segments in place) silently corrupt the stored
    /// original. <c>DebugGridPath</c> is not copied — it is diagnostic-only.
    /// </summary>
    public RoutedPath DeepCopy() => TranslatedCopy(0, 0);

    /// <summary>
    /// Creates an independent deep copy with every segment shifted by (dx, dy).
    /// A pure translation leaves angles, radii and sweeps untouched, so the exact
    /// shape — including manually edited bend radii — is preserved.
    /// </summary>
    /// <param name="dx">Shift along X in micrometers.</param>
    /// <param name="dy">Shift along Y in micrometers.</param>
    public RoutedPath TranslatedCopy(double dx, double dy)
    {
        var copy = new RoutedPath
        {
            IsBlockedFallback = IsBlockedFallback,
            IsInvalidGeometry = IsInvalidGeometry,
            IsPlaceholderGeometry = IsPlaceholderGeometry,
            ViolatesProcessMinBendRadius = ViolatesProcessMinBendRadius,
            PassesThroughComponent = PassesThroughComponent,
            IsDirectStyledRoute = IsDirectStyledRoute,
            DirectStyle = DirectStyle
        };

        foreach (var segment in Segments)
        {
            switch (segment)
            {
                case BendSegment bend:
                    copy.Segments.Add(new BendSegment(
                        bend.Center.X + dx,
                        bend.Center.Y + dy,
                        bend.RadiusMicrometers,
                        bend.StartAngleDegrees,
                        bend.SweepAngleDegrees));
                    break;
                case StraightSegment straight:
                    copy.Segments.Add(new StraightSegment(
                        straight.StartPoint.X + dx,
                        straight.StartPoint.Y + dy,
                        straight.EndPoint.X + dx,
                        straight.EndPoint.Y + dy,
                        straight.StartAngleDegrees));
                    break;
            }
        }

        return copy;
    }

    /// <summary>
    /// Checks if the path is valid (segments connect properly).
    /// </summary>
    public bool IsValid
    {
        get
        {
            if (Segments.Count == 0) return false;
            for (int i = 1; i < Segments.Count; i++)
            {
                var prev = Segments[i - 1];
                var curr = Segments[i];
                double dist = Math.Sqrt(Math.Pow(curr.StartPoint.X - prev.EndPoint.X, 2) +
                                        Math.Pow(curr.StartPoint.Y - prev.EndPoint.Y, 2));
                if (dist > 0.1) return false;
            }
            return true;
        }
    }
}
