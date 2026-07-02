using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Routing.AStarPathfinder;

namespace CAP_Core.Routing.CrossingInsertion;

/// <summary>
/// Detects right-angle intersections between a connection's direct path and
/// already-routed waveguides, validates the three crossing conditions
/// (orthogonality, straight stubs, free bounding box) and compares the
/// insertion loss of crossing versus detouring (LiDAR-style LRR decision).
/// </summary>
public class CrossingInserter
{
    /// <summary>Reference wavelength (nm) for reading the crossing through-loss from its S-matrix.</summary>
    public const int ReferenceWavelengthNm = 1550;

    /// <summary>Allowed angular deviation from a perfect axis-aligned right angle.</summary>
    public double OrthogonalityToleranceDegrees { get; set; } = 10.0;

    /// <summary>Extra straight run required beyond the crossing half-edge on each side (µm).</summary>
    public double StubClearanceMicrometers { get; set; } = 1.0;

    /// <summary>
    /// Reads the crossing through-transmission loss (dB) from the component's own
    /// S-matrix (west-port inflow → east-port outflow). Returns null when the
    /// component carries no usable S-matrix entry — in that case no crossing is
    /// inserted (conservative: keep the detour, never assume a loss value).
    /// </summary>
    public double? GetCrossingThroughLossDb(Component crossingComponent)
    {
        var westPin = CrossingPlacement.FindPinByAngle(crossingComponent, 180);
        var eastPin = CrossingPlacement.FindPinByAngle(crossingComponent, 0);
        if (westPin?.LogicalPin == null || eastPin?.LogicalPin == null) return null;

        // No fallback to another wavelength's matrix: using an arbitrary
        // wavelength's loss would be a silent physical assumption. Missing
        // reference wavelength → no crossing insertion (detours are kept).
        if (!crossingComponent.WaveLengthToSMatrixMap.TryGetValue(ReferenceWavelengthNm, out var sMatrix)
            || sMatrix == null)
            return null;

        var values = sMatrix.GetNonNullValues();
        if (!values.TryGetValue((westPin.LogicalPin.IDInFlow, eastPin.LogicalPin.IDOutFlow), out var through))
            return null;
        if (through.Magnitude <= 0) return null;

        return -20.0 * Math.Log10(through.Magnitude);
    }

    /// <summary>
    /// Computes the insertion loss (dB) of a path using the connection's loss parameters
    /// (mirrors <see cref="WaveguideConnection.RecalculateTransmission"/>).
    /// </summary>
    public static double ComputePathLossDb(RoutedPath path, WaveguideConnection connection)
    {
        double lossDbPerCm = connection.DispersionModel?.LossDbPerCmAt(ReferenceWavelengthNm)
                             ?? connection.PropagationLossDbPerCm;
        double propagationLoss = path.TotalLengthMicrometers / 10000.0 * lossDbPerCm;
        double bendLoss = path.TotalEquivalent90DegreeBends * connection.BendLossDbPer90Deg;
        return propagationLoss + bendLoss;
    }

    /// <summary>
    /// Searches the direct path for a single valid crossing opportunity.
    /// Returns null when the path crosses no waveguide, more than one waveguide,
    /// crosses at a non-right angle, lacks straight stubs, or when the crossing
    /// bounding box is blocked — in all those cases the detour is kept.
    /// </summary>
    /// <param name="connection">The connection whose direct path is evaluated.</param>
    /// <param name="directPath">Direct path routed with only component obstacles.</param>
    /// <param name="otherConnections">All other currently routed connections.</param>
    /// <param name="grid">Pathfinding grid used for the bounding-box clearance check.</param>
    /// <param name="crossingEdgeMicrometers">Edge length of the crossing component (µm).</param>
    /// <param name="crossingLossDb">Through-loss of one crossing (dB), from its S-matrix.</param>
    public CrossingCandidate? FindCandidate(
        WaveguideConnection connection,
        RoutedPath directPath,
        IEnumerable<WaveguideConnection> otherConnections,
        PathfindingGrid grid,
        double crossingEdgeMicrometers,
        double crossingLossDb)
    {
        var intersection = FindSingleIntersection(directPath, otherConnections);
        if (intersection == null) return null;

        var (other, newSegment, existingSegment, point) = intersection.Value;
        var newDirection = CrossingGeometry.GetDirection(newSegment);
        var existingDirection = CrossingGeometry.GetDirection(existingSegment);

        if (!CrossingGeometry.IsAxisAlignedRightAngle(
                newDirection, existingDirection, OrthogonalityToleranceDegrees, out bool newIsHorizontal))
            return null;

        double requiredRun = crossingEdgeMicrometers / 2.0 + StubClearanceMicrometers;
        if (!CrossingGeometry.HasStraightRunAround(newSegment, point, requiredRun)) return null;
        if (!CrossingGeometry.HasStraightRunAround(existingSegment, point, requiredRun)) return null;

        double halfBox = crossingEdgeMicrometers / 2.0 + grid.ObstaclePaddingMicrometers;
        var allowedIds = new HashSet<Guid> { connection.Id, other.Id };
        if (!grid.IsAreaClearForCrossing(
                point.X - halfBox, point.Y - halfBox,
                point.X + halfBox, point.Y + halfBox, allowedIds))
            return null;

        return new CrossingCandidate
        {
            NewConnection = connection,
            ExistingConnection = other,
            DirectPath = directPath,
            IntersectionPoint = point,
            NewConnectionIsHorizontal = newIsHorizontal,
            NewDirection = newDirection,
            ExistingDirection = existingDirection,
            CrossingVariantLossDb = ComputePathLossDb(directPath, connection) + crossingLossDb,
        };
    }

    /// <summary>
    /// Decides crossing vs. detour by insertion loss (lower wins).
    /// </summary>
    /// <param name="candidate">The validated crossing candidate.</param>
    /// <param name="detourLossDb">Loss of the current avoiding route (∞ when unroutable).</param>
    public bool IsCrossingBeneficial(CrossingCandidate candidate, double detourLossDb) =>
        candidate.CrossingVariantLossDb < detourLossDb;

    /// <summary>
    /// Finds the single interior intersection between the direct path and all other
    /// routed connections. Returns null when there is no intersection or more than one
    /// (multi-crossing insertion is not supported — the router keeps the detour).
    /// </summary>
    private static (WaveguideConnection Other, StraightSegment NewSegment,
                    StraightSegment ExistingSegment, (double X, double Y) Point)?
        FindSingleIntersection(RoutedPath directPath, IEnumerable<WaveguideConnection> otherConnections)
    {
        (WaveguideConnection, StraightSegment, StraightSegment, (double X, double Y))? found = null;

        foreach (var other in otherConnections)
        {
            if (other.RoutedPath == null || !other.IsPathValid) continue;

            foreach (var newSegment in directPath.Segments.OfType<StraightSegment>())
            {
                foreach (var existingSegment in other.RoutedPath.Segments.OfType<StraightSegment>())
                {
                    if (!CrossingGeometry.TryGetIntersection(newSegment, existingSegment, out var point))
                        continue;
                    if (found != null) return null; // more than one crossing → keep detour
                    found = (other, newSegment, existingSegment, point);
                }
            }
        }
        return found;
    }
}
