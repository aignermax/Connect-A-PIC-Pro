using CAP_Core.Routing;

namespace CAP_Core.Analysis;

/// <summary>
/// Adaptive bend-arc sampling used by <see cref="WaveguideSpacingGeometry"/> to
/// approximate distances involving <see cref="BendSegment"/>s.
/// </summary>
internal static class WaveguideSpacingSampler
{
    private const int MinimumBendSamples = 12;
    private const int MaximumBendSamples = 200;
    private const double BendSampleStepFactor = 0.25;

    internal static List<(double X, double Y)> SampleBend(BendSegment bend, double minSpacing)
    {
        int sampleCount = BendSampleCount(bend, minSpacing);
        var points = new List<(double X, double Y)>(sampleCount + 1);

        double startRad = bend.StartAngleDegrees * Math.PI / 180.0;
        double sweepRad = bend.SweepAngleDegrees * Math.PI / 180.0;
        double sign = Math.Sign(bend.SweepAngleDegrees);
        if (sign == 0) sign = 1;

        for (int i = 0; i <= sampleCount; i++)
        {
            double t = (double)i / sampleCount;
            double angle = startRad + sweepRad * t;
            double x = bend.Center.X + bend.RadiusMicrometers * Math.Cos(angle - Math.PI / 2.0 * sign);
            double y = bend.Center.Y + bend.RadiusMicrometers * Math.Sin(angle - Math.PI / 2.0 * sign);
            points.Add((x, y));
        }

        return points;
    }

    private static int BendSampleCount(BendSegment bend, double minSpacing)
    {
        double step = minSpacing * BendSampleStepFactor;
        if (step <= 0)
            return MinimumBendSamples;

        int count = (int)(bend.LengthMicrometers / step) + 1;
        return Math.Clamp(count, MinimumBendSamples, MaximumBendSamples);
    }
}
