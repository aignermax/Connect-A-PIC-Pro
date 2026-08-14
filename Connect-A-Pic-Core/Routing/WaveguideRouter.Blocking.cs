using CAP_Core.Routing.AStarPathfinder;

namespace CAP_Core.Routing;

/// <summary>
/// Obstacle-blocking checks for routed geometry: sampling straight lines and arcs
/// against the pathfinding grid's blocked-cell predicates.
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
