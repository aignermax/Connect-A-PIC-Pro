using CAP_Core.Routing;

namespace CAP_Core.Routing.AStarPathfinder;

/// <summary>
/// Bend creation mode for different routing scenarios.
/// </summary>
public enum BendMode
{
    /// <summary>
    /// Cardinal 90° bends only (clamp sweep angle to ±90°).
    /// Used for standard Manhattan routing.
    /// </summary>
    Cardinal90,

    /// <summary>
    /// Flexible bends with any sweep angle.
    /// Used for non-cardinal pin approaches and S-bends.
    /// </summary>
    Flexible,

    /// <summary>
    /// Limited to ±45° for gentle S-bend corrections.
    /// </summary>
    Limited45,

    /// <summary>
    /// Sweep snapped to the nearest ±45° multiple (max ±90°).
    /// Used for octile routing where corners are 45° or 90°.
    /// </summary>
    Diagonal45
}

/// <summary>
/// Builds circular bend segments with consistent geometry.
/// Eliminates duplicate bend creation code (AddBend vs AddFlexibleBend).
/// </summary>
public class BendBuilder
{
    /// <summary>Sweep angle above which the tangent length formula is clamped (tan(45°) = 1).</summary>
    private const double MaxTangentSweepDegrees = 90.0;

    private readonly double _minBendRadius;
    private readonly List<double> _allowedRadii;

    public BendBuilder(double minBendRadius, List<double>? allowedRadii = null)
    {
        _minBendRadius = minBendRadius;
        _allowedRadii = allowedRadii?.OrderBy(r => r).ToList() ?? new List<double>();
    }

    /// <summary>
    /// Finds the largest allowed radius that fits within the given distance.
    /// Returns 0 if no radius fits.
    /// </summary>
    /// <param name="maxDistance">Maximum available distance in micrometers.</param>
    /// <returns>Largest fitting radius, or 0 if none fits.</returns>
    public double FindLargestRadiusAtMost(double maxDistance)
    {
        if (_allowedRadii.Count == 0)
            return maxDistance >= _minBendRadius ? _minBendRadius : 0;

        double best = 0;
        foreach (var r in _allowedRadii)
        {
            if (r <= maxDistance)
                best = r;
        }
        return best;
    }

    /// <summary>
    /// Selects the LARGEST allowed radius whose arc fits the available straight runs on both
    /// sides of a corner — photonic bend loss shrinks rapidly with radius, so generous space
    /// should yield the gentlest bend. A radius fits when its tangent length
    /// (r·tan(sweep/2)) does not exceed either available run. Radii below the minimum are
    /// never chosen; when nothing larger fits, falls back to the classic smallest-viable
    /// selection so tight layouts keep their previous behavior.
    /// </summary>
    /// <param name="sweepDegrees">Turn angle of the corner in degrees.</param>
    /// <param name="incomingAvailableMicrometers">Free straight run before the corner apex.</param>
    /// <param name="outgoingAvailableMicrometers">Free straight run after the corner apex.</param>
    /// <param name="isRadiusRejected">Optional veto (e.g. obstacle collision) per candidate radius.</param>
    public double SelectLargestRadiusThatFits(
        double sweepDegrees,
        double incomingAvailableMicrometers,
        double outgoingAvailableMicrometers,
        Func<double, bool>? isRadiusRejected = null)
    {
        double fallback = SelectRadius(_minBendRadius);
        if (_allowedRadii.Count == 0)
            return fallback;

        double halfSweepRadians =
            Math.Min(Math.Abs(sweepDegrees), MaxTangentSweepDegrees) * Math.PI / 360.0;
        double available = Math.Min(incomingAvailableMicrometers, outgoingAvailableMicrometers);

        for (int i = _allowedRadii.Count - 1; i >= 0; i--)
        {
            double radius = _allowedRadii[i];
            if (radius <= fallback || radius < _minBendRadius)
                break;

            double tangentLength = radius * Math.Tan(halfSweepRadians);
            if (tangentLength > available)
                continue;
            if (isRadiusRejected?.Invoke(radius) == true)
                continue;

            return radius;
        }

        return fallback;
    }

    /// <summary>
    /// Builds a bend segment from current position and angle to target angle.
    /// </summary>
    /// <param name="x">Current X position (micrometers)</param>
    /// <param name="y">Current Y position (micrometers)</param>
    /// <param name="fromAngle">Current angle (degrees)</param>
    /// <param name="toAngle">Target angle (degrees)</param>
    /// <param name="mode">Bend mode (Cardinal90, Flexible, Limited45)</param>
    /// <param name="radiusOverride">Optional radius override (ignores allowed radii selection)</param>
    /// <returns>Bend segment, or null if no bend needed</returns>
    public BendSegment? BuildBend(double x, double y, double fromAngle, double toAngle,
                                   BendMode mode = BendMode.Cardinal90, double? radiusOverride = null)
    {
        double sweepAngle = AngleUtilities.NormalizeAngle(toAngle - fromAngle);

        // Apply mode-specific angle constraints
        sweepAngle = mode switch
        {
            BendMode.Cardinal90 => Math.Sign(sweepAngle) * 90.0,
            BendMode.Limited45 => Math.Clamp(sweepAngle, -45, 45),
            BendMode.Diagonal45 => Math.Clamp(Math.Round(sweepAngle / 45.0) * 45.0, -90, 90),
            BendMode.Flexible => sweepAngle,
            _ => sweepAngle
        };

        // No bend needed if angle is too small
        if (Math.Abs(sweepAngle) < 2)
            return null;

        // Select appropriate radius
        double radius = radiusOverride ?? SelectRadius(_minBendRadius);

        // Calculate bend direction
        double bendDir = Math.Sign(sweepAngle);
        if (bendDir == 0) bendDir = 1;

        // Calculate bend center (perpendicular to start direction)
        double startRad = fromAngle * Math.PI / 180;
        double perpX = -Math.Sin(startRad) * bendDir;
        double perpY = Math.Cos(startRad) * bendDir;

        double centerX = x + perpX * radius;
        double centerY = y + perpY * radius;

        return new BendSegment(centerX, centerY, radius, fromAngle, sweepAngle);
    }

    /// <summary>
    /// Selects the smallest radius from allowed radii that meets minimum requirement.
    /// Falls back to minimum radius if no allowed radii specified.
    /// </summary>
    /// <param name="minRequired">Minimum required radius</param>
    /// <returns>Selected radius</returns>
    private double SelectRadius(double minRequired)
    {
        if (_allowedRadii.Count == 0)
            return minRequired;

        // Find smallest allowed radius >= minimum
        foreach (var radius in _allowedRadii)
        {
            if (radius >= minRequired)
                return radius;
        }

        // All allowed radii too small, use largest available
        return _allowedRadii[^1];
    }

    /// <summary>
    /// Calculates the endpoint position after a bend.
    /// Useful for planning ahead without creating the segment.
    /// </summary>
    public (double x, double y) CalculateBendEndpoint(double x, double y, double fromAngle,
                                                       double toAngle, double radius)
    {
        double sweepAngle = AngleUtilities.NormalizeAngle(toAngle - fromAngle);
        double bendDir = Math.Sign(sweepAngle);
        if (bendDir == 0) bendDir = 1;

        // Calculate center
        double startRad = fromAngle * Math.PI / 180;
        double perpX = -Math.Sin(startRad) * bendDir;
        double perpY = Math.Cos(startRad) * bendDir;

        double centerX = x + perpX * radius;
        double centerY = y + perpY * radius;

        // Calculate endpoint
        double endRad = toAngle * Math.PI / 180;
        double endX = centerX - Math.Sin(endRad) * bendDir * radius;
        double endY = centerY + Math.Cos(endRad) * bendDir * radius;

        return (endX, endY);
    }
}
