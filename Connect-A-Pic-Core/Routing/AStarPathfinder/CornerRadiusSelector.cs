namespace CAP_Core.Routing.AStarPathfinder;

/// <summary>
/// Picks the bend radius for a smoothed A* corner: the LARGEST allowed radius whose arc fits
/// the free straight runs on both sides of the corner and does not sweep through blocked grid
/// cells. Larger radii mean lower photonic bend loss, so generous space yields the gentlest
/// bend; the selection shrinks toward the minimum only when space or obstacles force it.
/// </summary>
public class CornerRadiusSelector
{
    /// <summary>Arc sampling step as a fraction of the grid cell size.</summary>
    private const double ArcSampleStepCellFraction = 0.5;

    private readonly PathfindingGrid _grid;
    private readonly BendBuilder _bendBuilder;
    private readonly double _minBendRadius;

    public CornerRadiusSelector(PathfindingGrid grid, BendBuilder bendBuilder, double minBendRadius)
    {
        _grid = grid;
        _bendBuilder = bendBuilder;
        _minBendRadius = minBendRadius;
    }

    /// <summary>
    /// Selects the bend radius for the corner at <paramref name="cornerIndex"/>.
    /// </summary>
    /// <param name="corners">All corners of the grid path (grid coordinates).</param>
    /// <param name="cornerIndex">Index of the corner being smoothed.</param>
    /// <param name="lastTurnIndex">Index of the last turning corner (its apex targets the end pin).</param>
    /// <param name="x">Current position X before the corner's straight lead-in (µm).</param>
    /// <param name="y">Current position Y before the corner's straight lead-in (µm).</param>
    /// <param name="fromAngle">Heading into the corner (degrees).</param>
    /// <param name="toAngle">Heading out of the corner (degrees).</param>
    /// <param name="distanceToApex">Distance from (x, y) to the corner apex (µm).</param>
    /// <param name="apexX">Corner apex X (µm).</param>
    /// <param name="apexY">Corner apex Y (µm).</param>
    /// <param name="endX">End pin X (µm).</param>
    /// <param name="endY">End pin Y (µm).</param>
    public double SelectForCorner(
        List<(int X, int Y, GridDirection Direction)> corners, int cornerIndex, int lastTurnIndex,
        double x, double y, double fromAngle, double toAngle, double distanceToApex,
        double apexX, double apexY, double endX, double endY)
    {
        double turnAngle = Math.Abs(AngleUtilities.NormalizeAngle(toAngle - fromAngle));
        double outgoingRun = OutgoingRunLength(corners, cornerIndex, apexX, apexY, endX, endY, lastTurnIndex);
        return _bendBuilder.SelectLargestRadiusThatFits(
            turnAngle, distanceToApex, outgoingRun,
            r => IsCandidateBendBlocked(x, y, fromAngle, toAngle, distanceToApex, r, turnAngle));
    }

    /// <summary>
    /// Free straight run available AFTER a corner's apex: the distance to the next corner
    /// (or to the end pin for the last turn). Before the last turn a minimum-radius tangent
    /// is reserved so the following corner can always realize at least the minimum bend.
    /// </summary>
    private double OutgoingRunLength(
        List<(int X, int Y, GridDirection Direction)> corners, int cornerIndex,
        double apexX, double apexY, double endX, double endY, int lastTurnIndex)
    {
        double nextX, nextY;
        bool reserveNextBend = cornerIndex < lastTurnIndex && cornerIndex + 1 < corners.Count;
        if (reserveNextBend)
            (nextX, nextY) = _grid.GridToPhysical(corners[cornerIndex + 1].X, corners[cornerIndex + 1].Y);
        else
            (nextX, nextY) = (endX, endY);

        double run = Math.Sqrt(Math.Pow(nextX - apexX, 2) + Math.Pow(nextY - apexY, 2));
        return reserveNextBend ? run - _minBendRadius : run;
    }

    /// <summary>
    /// Vetoes a candidate corner radius whose arc would sweep through blocked grid cells.
    /// Larger radii cut deeper into the corner's inside, into cells the A* path never
    /// visited, so each candidate arc is sampled against the grid (endpoints excluded —
    /// they legitimately touch pin corridors).
    /// </summary>
    private bool IsCandidateBendBlocked(
        double x, double y, double fromAngle, double toAngle,
        double distanceToApex, double radius, double turnAngleDegrees)
    {
        double setback = radius * Math.Tan(turnAngleDegrees * Math.PI / 360.0);
        double angleRad = fromAngle * Math.PI / 180;
        double bendStartX = x + (distanceToApex - setback) * Math.Cos(angleRad);
        double bendStartY = y + (distanceToApex - setback) * Math.Sin(angleRad);

        var bend = _bendBuilder.BuildBend(bendStartX, bendStartY, fromAngle, toAngle,
                                          BendMode.Diagonal45, radius);
        if (bend == null) return false;

        double stepLength = _grid.CellSizeMicrometers * ArcSampleStepCellFraction;
        var samples = ArcSampling.SamplePoints(bend, stepLength).ToList();
        for (int i = 1; i < samples.Count - 1; i++)
        {
            var (gx, gy) = _grid.PhysicalToGrid(samples[i].X, samples[i].Y);
            if (_grid.IsBlocked(gx, gy)) return true;
        }
        return false;
    }
}
