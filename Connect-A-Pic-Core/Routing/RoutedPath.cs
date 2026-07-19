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
    /// True when the path could only be routed with a bend radius below the active
    /// fabrication process' minimum (<see cref="WaveguideRouter.ProcessMinBendRadiusMicrometers"/>).
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
