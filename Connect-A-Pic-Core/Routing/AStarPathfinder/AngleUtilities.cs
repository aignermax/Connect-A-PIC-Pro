namespace CAP_Core.Routing.AStarPathfinder;

/// <summary>
/// Centralized angle manipulation utilities with consistent tolerances.
/// Eliminates scattered angle logic and magic number tolerances.
/// </summary>
public static class AngleUtilities
{
    /// <summary>
    /// Default angular tolerance for comparing angles (in degrees).
    /// Used consistently across all angle comparisons.
    /// </summary>
    public const double DefaultAngleTolerance = 10.0;

    /// <summary>
    /// Normalizes an angle to the range (-range, range].
    /// </summary>
    /// <param name="angle">Angle in degrees</param>
    /// <param name="range">Range limit (default 180 for [-180, 180])</param>
    /// <returns>Normalized angle</returns>
    public static double NormalizeAngle(double angle, double range = 180)
    {
        while (angle > range) angle -= 360;
        while (angle <= -range) angle += 360;
        return angle;
    }

    /// <summary>
    /// Checks if two angles are close within tolerance.
    /// </summary>
    /// <param name="angle1">First angle in degrees</param>
    /// <param name="angle2">Second angle in degrees</param>
    /// <param name="tolerance">Tolerance in degrees</param>
    /// <returns>True if angles differ by less than tolerance</returns>
    public static bool IsAngleClose(double angle1, double angle2, double tolerance = DefaultAngleTolerance)
    {
        return Math.Abs(NormalizeAngle(angle1 - angle2)) < tolerance;
    }

    /// <summary>
    /// Checks if an angle is cardinal (0°, 90°, 180°, or 270°).
    /// </summary>
    /// <param name="angle">Angle in degrees</param>
    /// <param name="tolerance">Tolerance in degrees</param>
    /// <returns>True if angle is close to a cardinal direction</returns>
    public static bool IsCardinal(double angle, double tolerance = DefaultAngleTolerance)
    {
        angle = NormalizeAngle(angle);
        return Math.Abs(angle) < tolerance ||
               Math.Abs(angle - 90) < tolerance ||
               Math.Abs(angle - 180) < tolerance ||
               Math.Abs(angle + 180) < tolerance ||
               Math.Abs(angle - 270) < tolerance ||
               Math.Abs(angle + 90) < tolerance;
    }

    /// <summary>
    /// Checks if an angle is horizontal (0° or 180°).
    /// </summary>
    /// <param name="angle">Angle in degrees</param>
    /// <param name="tolerance">Tolerance in degrees</param>
    /// <returns>True if angle is close to horizontal</returns>
    public static bool IsHorizontal(double angle, double tolerance = DefaultAngleTolerance)
    {
        angle = NormalizeAngle(angle);
        return Math.Abs(angle) < tolerance || Math.Abs(Math.Abs(angle) - 180) < tolerance;
    }

    /// <summary>
    /// Checks if an angle is vertical (90° or 270°).
    /// </summary>
    /// <param name="angle">Angle in degrees</param>
    /// <param name="tolerance">Tolerance in degrees</param>
    /// <returns>True if angle is close to vertical</returns>
    public static bool IsVertical(double angle, double tolerance = DefaultAngleTolerance)
    {
        angle = NormalizeAngle(angle);
        return Math.Abs(angle - 90) < tolerance || Math.Abs(angle + 90) < tolerance;
    }

    /// <summary>
    /// Quantizes an angle to the nearest cardinal direction (0°, 90°, 180°, 270°).
    /// </summary>
    /// <param name="angle">Input angle in degrees</param>
    /// <returns>Nearest cardinal angle</returns>
    public static double QuantizeToCardinal(double angle)
    {
        angle = NormalizeAngle(angle);

        // Symmetric ranges around each cardinal direction
        if (angle >= -45 && angle < 45)
            return 0;    // East
        if (angle >= 45 && angle < 135)
            return 90;   // North
        if (angle >= 135 || angle < -135)
            return 180;  // West
        // angle >= -135 && angle < -45
        return 270;      // South
    }

    /// <summary>
    /// Quantizes an angle to the nearest 45° step (0°, 45°, 90°, ..., 315°).
    /// </summary>
    /// <param name="angle">Input angle in degrees</param>
    /// <returns>Nearest 45° multiple in the range [0, 360)</returns>
    public static double QuantizeTo45(double angle)
    {
        // Normalize to [0, 360)
        angle %= 360;
        if (angle < 0) angle += 360;

        double quantized = Math.Round(angle / GridDirectionExtensions.AngleStepDegrees)
                           * GridDirectionExtensions.AngleStepDegrees;
        return quantized % 360;
    }

    /// <summary>
    /// Converts a GridDirection to angle in degrees.
    /// </summary>
    /// <param name="direction">Grid direction</param>
    /// <returns>Angle in degrees (0=East, 45=NorthEast, 90=North, ...)</returns>
    public static double DirectionToAngle(GridDirection direction)
    {
        return direction.GetAngleDegrees();
    }

    /// <summary>
    /// Converts an angle in degrees to the nearest of the 8 grid directions.
    /// </summary>
    /// <param name="angle">Angle in degrees</param>
    /// <returns>Nearest grid direction (45° sectors)</returns>
    public static GridDirection AngleToDirection(double angle)
    {
        return GridDirectionExtensions.FromAngle(angle);
    }
}
