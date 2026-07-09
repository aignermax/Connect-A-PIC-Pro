namespace CAP_Core.Routing.MetalRouting;

/// <summary>
/// Finds the points where an electrical metal trace crosses optical waveguide paths
/// (issue #682). Used at export time to place bridge elements when the active process
/// requires them (<see cref="ElectricalCrossingPolicy.BridgeRequired"/>).
/// Segments are compared by their start/end chords — for bend segments this is a
/// conservative straight-line approximation, which is adequate because bridge markers
/// only need to land on (not exactly at the apex of) the crossing region.
/// </summary>
public static class WaveguideCrossingDetector
{
    /// <summary>Tolerance in µm below which two segment endpoints count as touching, not crossing.</summary>
    private const double EndpointToleranceMicrometers = 1e-6;

    /// <summary>
    /// Returns every point (in editor/app coordinates, µm) where the given metal trace
    /// crosses one of the optical paths.
    /// </summary>
    /// <param name="metalSegments">Routed segments of the electrical trace.</param>
    /// <param name="opticalPaths">Routed segment lists of all optical connections.</param>
    public static IReadOnlyList<(double X, double Y)> FindCrossings(
        IReadOnlyList<PathSegment> metalSegments,
        IEnumerable<IReadOnlyList<PathSegment>> opticalPaths)
    {
        var crossings = new List<(double X, double Y)>();
        foreach (var opticalPath in opticalPaths)
        {
            foreach (var metalSegment in metalSegments)
            {
                foreach (var opticalSegment in opticalPath)
                {
                    if (TryIntersect(metalSegment, opticalSegment, out var point))
                        crossings.Add(point);
                }
            }
        }
        return crossings;
    }

    /// <summary>
    /// Intersects the chords of two segments. Returns true when they properly cross
    /// (shared endpoints within tolerance do not count).
    /// </summary>
    private static bool TryIntersect(PathSegment first, PathSegment second, out (double X, double Y) point)
    {
        point = default;
        var (p1, p2) = (first.StartPoint, first.EndPoint);
        var (q1, q2) = (second.StartPoint, second.EndPoint);

        double rX = p2.X - p1.X, rY = p2.Y - p1.Y;
        double sX = q2.X - q1.X, sY = q2.Y - q1.Y;
        double denominator = rX * sY - rY * sX;
        if (Math.Abs(denominator) < EndpointToleranceMicrometers)
            return false; // Parallel or degenerate — no proper crossing.

        double qpX = q1.X - p1.X, qpY = q1.Y - p1.Y;
        double t = (qpX * sY - qpY * sX) / denominator;
        double u = (qpX * rY - qpY * rX) / denominator;

        // Interior-only: crossings at the very segment ends are pin contacts, not bridges.
        const double interiorEpsilon = 1e-9;
        if (t <= interiorEpsilon || t >= 1 - interiorEpsilon ||
            u <= interiorEpsilon || u >= 1 - interiorEpsilon)
            return false;

        point = (p1.X + t * rX, p1.Y + t * rY);
        return true;
    }
}
