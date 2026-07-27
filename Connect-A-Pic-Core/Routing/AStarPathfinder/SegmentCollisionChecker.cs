namespace CAP_Core.Routing.AStarPathfinder;

/// <summary>
/// Samples physical path segments against the pathfinding grid to detect
/// collisions with blocked cells (components or other waveguides). Used to
/// validate geometry that is constructed outside the collision-checked A*
/// search — e.g. the path smoother's terminal approach, which was previously
/// built purely geometrically and could cut through a neighboring waveguide
/// near the pin (issue #704, bug 3).
/// </summary>
public class SegmentCollisionChecker
{
    private readonly PathfindingGrid _grid;
    private readonly HashSet<(int x, int y)>? _excludedCells;

    /// <summary>Fraction of a cell used as the sampling step along segments.</summary>
    private const double SampleStepCellFraction = 0.5;

    /// <summary>Minimum number of samples along a bend arc.</summary>
    private const int MinArcSamples = 10;

    /// <summary>Creates a checker over the given pathfinding grid.</summary>
    public SegmentCollisionChecker(PathfindingGrid grid)
    {
        _grid = grid;
    }

    /// <summary>
    /// Creates a checker that ignores the given cells — the obstacle cells of the
    /// route's own endpoint components. A terminal approach necessarily enters the
    /// component of the pin it lands on (its pins can sit deep inside the body), so
    /// those cells are not real collisions; only foreign components and waveguides
    /// count (issue #704 review — fixes terminal-approach false positives).
    /// </summary>
    public SegmentCollisionChecker(PathfindingGrid grid, HashSet<(int x, int y)>? excludedCells)
    {
        _grid = grid;
        _excludedCells = excludedCells;
    }

    /// <summary>
    /// Checks whether any of the given segments passes through a blocked cell.
    /// One cell of margin is skipped at each segment end so cells legitimately
    /// touched at joints with the preceding segment or at the pin itself do
    /// not count as collisions.
    /// </summary>
    public bool IsAnyBlocked(IEnumerable<PathSegment> segments)
    {
        foreach (var segment in segments)
        {
            bool blocked = segment switch
            {
                StraightSegment => IsStraightBlocked(segment),
                BendSegment bend => IsBendBlocked(bend),
                _ => false,
            };
            if (blocked)
                return true;
        }
        return false;
    }

    /// <summary>Samples a straight segment (excluding one cell at each end).</summary>
    private bool IsStraightBlocked(PathSegment segment)
    {
        double dx = segment.EndPoint.X - segment.StartPoint.X;
        double dy = segment.EndPoint.Y - segment.StartPoint.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        double margin = _grid.CellSizeMicrometers;
        if (length <= margin * 2)
            return false;

        dx /= length;
        dy /= length;
        double step = _grid.CellSizeMicrometers * SampleStepCellFraction;

        for (double t = margin; t < length - margin; t += step)
        {
            if (IsPointBlocked(segment.StartPoint.X + dx * t, segment.StartPoint.Y + dy * t))
                return true;
        }
        return false;
    }

    /// <summary>Samples a bend arc (excluding one cell of arc length at each end).</summary>
    private bool IsBendBlocked(BendSegment bend)
    {
        double arcLength = bend.LengthMicrometers;
        double margin = _grid.CellSizeMicrometers;
        if (arcLength <= margin * 2)
            return false;

        double startRad = bend.StartAngleDegrees * Math.PI / 180;
        double sweepRad = bend.SweepAngleDegrees * Math.PI / 180;
        double step = _grid.CellSizeMicrometers * SampleStepCellFraction;
        int numSamples = Math.Max(MinArcSamples, (int)Math.Ceiling(arcLength / step));

        double sign = Math.Sign(bend.SweepAngleDegrees);
        if (sign == 0) sign = 1;

        double tMargin = margin / arcLength;
        for (int i = 1; i < numSamples; i++)
        {
            double t = (double)i / numSamples;
            if (t < tMargin || t > 1 - tMargin)
                continue;

            double angle = startRad + sweepRad * t;
            double px = bend.Center.X + bend.RadiusMicrometers * Math.Cos(angle - Math.PI / 2 * sign);
            double py = bend.Center.Y + bend.RadiusMicrometers * Math.Sin(angle - Math.PI / 2 * sign);
            if (IsPointBlocked(px, py))
                return true;
        }
        return false;
    }

    private bool IsPointBlocked(double physicalX, double physicalY)
    {
        var (gx, gy) = _grid.PhysicalToGrid(physicalX, physicalY);
        if (_excludedCells != null && _excludedCells.Contains((gx, gy)))
            return false; // the route's own endpoint component — not a collision
        return _grid.IsBlocked(gx, gy);
    }
}
