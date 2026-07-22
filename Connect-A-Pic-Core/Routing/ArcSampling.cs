namespace CAP_Core.Routing;

/// <summary>
/// Samples <see cref="BendSegment"/> arcs into points spaced by ARC LENGTH.
/// Obstacle rasterization and collision checks must use this instead of angle-based
/// sampling: a fixed number of samples per degree leaves gaps larger than a grid cell
/// on large-radius arcs, so the arc reads as a dotted line instead of a solid obstacle.
/// </summary>
public static class ArcSampling
{
    /// <summary>
    /// Returns points along the arc, including both endpoints, spaced at most
    /// <paramref name="maxStepMicrometers"/> apart along the arc.
    /// </summary>
    /// <param name="bend">The arc segment to sample.</param>
    /// <param name="maxStepMicrometers">Maximum arc-length distance between samples (µm).</param>
    public static IEnumerable<(double X, double Y)> SamplePoints(BendSegment bend, double maxStepMicrometers)
    {
        double startRad = bend.StartAngleDegrees * Math.PI / 180.0;
        double sweepRad = bend.SweepAngleDegrees * Math.PI / 180.0;
        double arcLength = Math.Abs(sweepRad) * bend.RadiusMicrometers;
        double step = Math.Max(maxStepMicrometers, 1e-6);
        int steps = Math.Max(1, (int)Math.Ceiling(arcLength / step));

        double sign = Math.Sign(bend.SweepAngleDegrees);
        if (sign == 0) sign = 1;
        // The arc point sits perpendicular to the tangent angle, on the turn side
        // (same convention as the BendSegment constructor).
        double perpOffset = Math.PI / 2 * sign;

        for (int i = 0; i <= steps; i++)
        {
            double angle = startRad + sweepRad * i / steps;
            yield return (bend.Center.X + bend.RadiusMicrometers * Math.Cos(angle - perpOffset),
                          bend.Center.Y + bend.RadiusMicrometers * Math.Sin(angle - perpOffset));
        }
    }
}
