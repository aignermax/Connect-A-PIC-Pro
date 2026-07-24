using CAP_Core.Components.Core;
using CAP_Core.Routing.AStarPathfinder;

namespace CAP_Core.Routing;

/// <summary>
/// Validates a design-file cached route against the CURRENT pin calibration of its
/// endpoints. PDK pin data can change between releases (round-5 example: the DC
/// Halfring's port angles were corrected from 90° to 270° — the old values produced a
/// 9.96 µm wrong GDS export). The endpoints of a saved route still coincide with the
/// unchanged pin positions, so the incremental router would keep the stale geometry —
/// but the waveguide then leaves/enters the port against its declared direction and the
/// GDS export kinks into the component. This check detects the direction mismatch so
/// the load path can discard the cached geometry, drop its frozen state and re-route.
/// </summary>
public static class CachedRouteValidator
{
    /// <summary>
    /// Maximum deviation between a path's docking direction and the pin's current
    /// angle. A* quantizes the launch direction to 45° steps (up to 22.5° legitimate
    /// slack); a calibration fix flips by 90° or 180°. 60° separates the two regimes.
    /// </summary>
    public const double DockingAngleToleranceDegrees = 60.0;

    /// <summary>
    /// Checks whether the cached path leaves the start pin along its current absolute
    /// angle and enters the end pin against its current absolute angle (the
    /// <c>endAngle + 180°</c> convention used by the router). Empty paths,
    /// blocked-fallback paths (Manhattan emergency geometry ignores pin directions by
    /// design) and electrical routes (metal traces have no optical docking rule)
    /// always report a match.
    /// </summary>
    public static (bool StartMatches, bool EndMatches) CheckPinDirections(
        PhysicalPin startPin, PhysicalPin endPin, RoutedPath path)
    {
        if (path.Segments.Count == 0 || path.IsBlockedFallback)
            return (true, true);
        if (startPin.MatterType == MatterType.Electricity ||
            endPin.MatterType == MatterType.Electricity)
            return (true, true);

        // Straight segments derive their direction from the geometry instead of the
        // stored angle: legacy files may carry a defaulted 0° angle field, and the
        // launch/entry segment of a routed path is a straight in practice.
        var startDirection = SegmentStartDirection(path.Segments[0]);
        var endDirection = SegmentEndDirection(path.Segments[^1]);

        bool startMatches = startDirection is not { } start ||
            AngleDifference(start, startPin.GetAbsoluteAngle()) <= DockingAngleToleranceDegrees;
        bool endMatches = endDirection is not { } end ||
            AngleDifference(end, endPin.GetAbsoluteAngle() + 180.0) <= DockingAngleToleranceDegrees;
        return (startMatches, endMatches);
    }

    private static double? SegmentStartDirection(PathSegment segment) => segment switch
    {
        StraightSegment s => GeometricDirection(s.StartPoint, s.EndPoint),
        _ => segment.StartAngleDegrees
    };

    private static double? SegmentEndDirection(PathSegment segment) => segment switch
    {
        StraightSegment s => GeometricDirection(s.StartPoint, s.EndPoint),
        _ => segment.EndAngleDegrees
    };

    /// <summary>Direction from a to b in degrees, or null for a degenerate segment.</summary>
    private static double? GeometricDirection((double X, double Y) a, (double X, double Y) b)
    {
        const double DegenerateLengthMicrometers = 1e-6;
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        if (Math.Sqrt(dx * dx + dy * dy) < DegenerateLengthMicrometers)
            return null;
        return Math.Atan2(dy, dx) * 180.0 / Math.PI;
    }

    private static double AngleDifference(double a, double b) =>
        Math.Abs(AngleUtilities.NormalizeAngle(a - b));
}
