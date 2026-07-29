namespace CAP_Core.Routing.AStarPathfinder;

/// <summary>
/// Detects self-intersecting A* grid paths. A waveguide that crosses itself
/// (e.g. a full 360° loop at the source pin) has no valid optical model, so
/// such paths must never be returned by the pathfinder.
/// </summary>
public static class PathLoopDetector
{
    /// <summary>
    /// Returns true when the path visits any grid cell twice, or when two
    /// diagonal steps cross inside the same unit square (X pattern) without
    /// sharing a cell — both mean the resulting waveguide would intersect itself.
    /// </summary>
    public static bool IsSelfIntersecting(IReadOnlyList<AStarNode> path)
    {
        if (path.Count < 2)
            return false;

        var visitedCells = new HashSet<(int X, int Y)>();
        foreach (var node in path)
        {
            if (!visitedCells.Add((node.X, node.Y)))
                return true;
        }

        return HasCrossingDiagonalSteps(path);
    }

    /// <summary>
    /// Two diagonal steps of opposite slope through the same unit square cross
    /// each other between cells, so a duplicate-cell check alone misses them.
    /// Each diagonal step is identified by the square it traverses (its
    /// minimum corner) and its slope sign.
    /// </summary>
    private static bool HasCrossingDiagonalSteps(IReadOnlyList<AStarNode> path)
    {
        var diagonalSteps = new HashSet<(int SquareX, int SquareY, bool PositiveSlope)>();
        for (int i = 1; i < path.Count; i++)
        {
            int dx = path[i].X - path[i - 1].X;
            int dy = path[i].Y - path[i - 1].Y;
            if (dx == 0 || dy == 0)
                continue;

            int squareX = Math.Min(path[i].X, path[i - 1].X);
            int squareY = Math.Min(path[i].Y, path[i - 1].Y);
            bool positiveSlope = dx == dy;

            if (diagonalSteps.Contains((squareX, squareY, !positiveSlope)))
                return true;
            diagonalSteps.Add((squareX, squareY, positiveSlope));
        }
        return false;
    }
}
