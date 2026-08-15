using CAP_Core.Routing.AStarPathfinder;

namespace CAP_Core.Routing;

/// <summary>
/// The abutment half of <see cref="WaveguideRouter"/> (split out to keep the router below
/// the file-size limit): pins closer than <see cref="AbutmentThresholdMicrometers"/> are a
/// perfect abutment, not a waveguide — gdsfactory-style touching cells or two components
/// snapped pin-to-pin on the canvas. They get the minimal pin-to-pin butt joint with no
/// fallback flag; the CSC fallback used to flag these degenerate routes
/// <see cref="RoutedPath.IsBlockedFallback"/>, plastering valid abutments with false
/// BlockedPath issues.
/// </summary>
public partial class WaveguideRouter
{
    /// <summary>
    /// Pin-to-pin distance (µm) below which a connection is a perfect abutment rather than
    /// a waveguide: the pins coincide, so there is nothing to route. Aligned with the
    /// frozen-path endpoint tolerance
    /// (<see cref="Components.Connections.WaveguideConnection.FrozenEndpointToleranceMicrometers"/>).
    /// </summary>
    public const double AbutmentThresholdMicrometers = 1.0;

    /// <summary>
    /// When the pins sit within <see cref="AbutmentThresholdMicrometers"/> of each other,
    /// builds the minimal butt joint: the exact pin-to-pin straight (zero-length when the
    /// pins coincide exactly), with no fallback flag — a perfect abutment is valid
    /// geometry, not a blocked route.
    /// </summary>
    /// <returns>True (with the butt joint in <paramref name="path"/>) for an abutment.</returns>
    private static bool TryRouteAbutment(
        double startX, double startY, double endX, double endY, out RoutedPath path)
    {
        double dx = endX - startX;
        double dy = endY - startY;
        if (dx * dx + dy * dy >= AbutmentThresholdMicrometers * AbutmentThresholdMicrometers)
        {
            path = new RoutedPath();
            return false;
        }

        double headingDegrees = dx == 0 && dy == 0
            ? 0.0
            : AngleUtilities.NormalizeAngle(Math.Atan2(dy, dx) * 180.0 / Math.PI);

        path = new RoutedPath();
        path.Segments.Add(new StraightSegment(startX, startY, endX, endY, headingDegrees));
        return true;
    }
}
