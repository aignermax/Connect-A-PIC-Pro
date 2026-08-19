using CAP_Core.Routing.AStarPathfinder;

namespace CAP_Core.Routing;

/// <summary>
/// Cell-level collision check between path segments and a pathfinding grid.
/// Shared by the path smoother's terminal-approach validation and any other caller
/// that needs to test whether a segment enters blocked cells. Segment endpoints are
/// skipped because they legitimately sit in pin corridors cleared for the route.
/// </summary>
public static class SegmentGridCollision
{
    /// <summary>
    /// True when the segment (straight or bend) enters a blocked cell.
    /// </summary>
    public static bool IsSegmentBlocked(PathfindingGrid grid, PathSegment segment)
    {
        if (segment is StraightSegment straight)
        {
            return IsLineBlocked(grid, straight.StartPoint.X, straight.StartPoint.Y,
                                 straight.EndPoint.X, straight.EndPoint.Y);
        }
        if (segment is BendSegment bend)
        {
            return IsArcBlocked(grid, bend);
        }
        return false;
    }

    /// <summary>True when a straight line enters a blocked cell (endpoints skipped).</summary>
    public static bool IsLineBlocked(PathfindingGrid grid, double x1, double y1, double x2, double y2)
    {
        double dx = x2 - x1;
        double dy = y2 - y1;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 0.001) return false;

        dx /= length;
        dy /= length;

        double stepSize = grid.CellSizeMicrometers * 0.5;
        double margin = grid.CellSizeMicrometers;

        for (double t = margin; t < length - margin; t += stepSize)
        {
            var (gx, gy) = grid.PhysicalToGrid(x1 + dx * t, y1 + dy * t);
            if (grid.IsBlocked(gx, gy)) return true;
        }
        return false;
    }

    /// <summary>True when an arc enters a blocked cell (endpoints skipped).</summary>
    public static bool IsArcBlocked(PathfindingGrid grid, BendSegment bend)
    {
        var samples = ArcSampling.SamplePoints(bend, grid.CellSizeMicrometers * 0.5).ToList();
        for (int i = 1; i < samples.Count - 1; i++)
        {
            var (gx, gy) = grid.PhysicalToGrid(samples[i].X, samples[i].Y);
            if (grid.IsBlocked(gx, gy)) return true;
        }
        return false;
    }
}
