using CAP_Core.Components.Core;

namespace CAP_Core.Routing;

/// <summary>
/// Read-only path validation predicates of <see cref="WaveguideRouter"/>: whether an existing
/// or candidate route passes through blocked grid cells, either against ALL obstacles, against
/// component cells only, or with the own-pin-corridor tolerance for routes hugging their pins.
/// </summary>
public partial class WaveguideRouter
{
    /// <summary>
    /// Checks if any segment in a path passes through blocked cells.
    /// </summary>
    public bool IsPathBlocked(IEnumerable<PathSegment> segments)
    {
        if (PathfindingGrid == null) return false;
        return IsPathBlocked(segments, PathfindingGrid.IsBlocked);
    }

    /// <summary>
    /// Checks if any segment in a path passes through cells blocked by COMPONENTS
    /// (including frozen group paths), ignoring registered waveguide obstacles.
    /// Use this to judge component collisions of an existing route regardless of
    /// which sibling routes are currently in the grid.
    /// </summary>
    public bool IsPathBlockedByComponents(IEnumerable<PathSegment> segments)
    {
        if (PathfindingGrid == null) return false;
        return IsPathBlocked(segments, PathfindingGrid.IsBlockedByComponent);
    }

    /// <summary>
    /// Like <see cref="IsPathBlockedByComponents(IEnumerable{PathSegment})"/>, but tolerant of
    /// the route's OWN endpoint pins: cells covered only by a neighbouring component's padding
    /// inside the persistent corridor of <paramref name="startPin"/> or <paramref name="endPin"/>
    /// do not count as a collision. A foreign component BODY inside the corridor still blocks.
    /// Use this to judge an existing route that may legitimately hug its pins.
    /// </summary>
    /// <param name="segments">The route's segments.</param>
    /// <param name="startPin">The route's start pin (its corridor is tolerated).</param>
    /// <param name="endPin">The route's end pin (its corridor is tolerated).</param>
    public bool IsPathBlockedByComponents(
        IEnumerable<PathSegment> segments, PhysicalPin? startPin, PhysicalPin? endPin)
    {
        if (PathfindingGrid == null) return false;
        var routePins = new List<PhysicalPin>(2);
        if (startPin != null) routePins.Add(startPin);
        if (endPin != null) routePins.Add(endPin);
        if (routePins.Count == 0)
            return IsPathBlockedByComponents(segments);
        return IsPathBlocked(segments,
            (gx, gy) => PathfindingGrid.IsBlockedByComponentForRoute(gx, gy, routePins));
    }

    /// <summary>Checks all segments against the given cell-blocked predicate.</summary>
    private bool IsPathBlocked(IEnumerable<PathSegment> segments, Func<int, int, bool> isCellBlocked)
    {
        foreach (var segment in segments)
        {
            if (segment is StraightSegment)
            {
                if (IsLineBlocked(segment.StartPoint.X, segment.StartPoint.Y,
                                  segment.EndPoint.X, segment.EndPoint.Y, isCellBlocked))
                    return true;
            }
            else if (segment is BendSegment bend)
            {
                if (IsArcBlocked(bend, isCellBlocked)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Checks if a straight line passes through any blocked cells.
    /// </summary>
    private bool IsLineBlocked(double x1, double y1, double x2, double y2,
                               Func<int, int, bool> isCellBlocked)
    {
        if (PathfindingGrid == null) return false;

        double dx = x2 - x1;
        double dy = y2 - y1;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 0.001) return false;

        dx /= length;
        dy /= length;

        double stepSize = PathfindingGrid.CellSizeMicrometers * 0.5;
        double margin = PathfindingGrid.CellSizeMicrometers;

        for (double t = margin; t < length - margin; t += stepSize)
        {
            double px = x1 + dx * t;
            double py = y1 + dy * t;
            var (gx, gy) = PathfindingGrid.PhysicalToGrid(px, py);
            if (isCellBlocked(gx, gy)) return true;
        }
        return false;
    }

    /// <summary>
    /// Checks if an arc segment passes through blocked cells.
    /// The arc endpoints themselves are skipped (they legitimately touch pin corridors).
    /// </summary>
    private bool IsArcBlocked(BendSegment bend, Func<int, int, bool> isCellBlocked)
    {
        if (PathfindingGrid == null) return false;

        double stepLength = PathfindingGrid.CellSizeMicrometers * 0.5;
        var samples = ArcSampling.SamplePoints(bend, stepLength).ToList();

        for (int i = 1; i < samples.Count - 1; i++)
        {
            var (gx, gy) = PathfindingGrid.PhysicalToGrid(samples[i].X, samples[i].Y);
            if (isCellBlocked(gx, gy)) return true;
        }
        return false;
    }
}
