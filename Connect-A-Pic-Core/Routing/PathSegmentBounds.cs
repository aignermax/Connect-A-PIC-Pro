namespace CAP_Core.Routing;

/// <summary>
/// Tight axis-aligned bounds of path segments. Straights are exact; arcs
/// contribute their endpoints plus the cardinal extreme points the sweep
/// actually crosses — NOT the full circle: a full-circle box around a generous
/// S-bend radius inflates group bounds and move-collision footprints far past
/// the real geometry (field report: "the collision region is ~5× the group",
/// blocking group moves). The conservative circle stays fine for render
/// culling (never under-cover there); collision and group sizing need truth.
/// </summary>
public static class PathSegmentBounds
{
    /// <summary>Tight bounds of one segment, expanded by <paramref name="paddingUm"/> per side.</summary>
    public static (double MinX, double MinY, double MaxX, double MaxY) Of(
        PathSegment segment, double paddingUm = 0)
    {
        if (segment is BendSegment bend)
            return BendBounds(bend, paddingUm);

        return (Math.Min(segment.StartPoint.X, segment.EndPoint.X) - paddingUm,
                Math.Min(segment.StartPoint.Y, segment.EndPoint.Y) - paddingUm,
                Math.Max(segment.StartPoint.X, segment.EndPoint.X) + paddingUm,
                Math.Max(segment.StartPoint.Y, segment.EndPoint.Y) + paddingUm);
    }

    /// <summary>
    /// Tight bounds of an arc: endpoints plus the axis extremes (0°/90°/180°/270°
    /// radial directions) the sweep crosses. Radial angle = tangent angle −
    /// 90°·sign(sweep), matching <see cref="BendSegment"/>'s construction.
    /// </summary>
    private static (double MinX, double MinY, double MaxX, double MaxY) BendBounds(
        BendSegment bend, double paddingUm)
    {
        double sign = Math.Sign(bend.SweepAngleDegrees);
        double startRadial = bend.StartAngleDegrees - (90.0 * sign);
        double span = Math.Abs(bend.SweepAngleDegrees);
        double r = bend.RadiusMicrometers;
        double cx = bend.Center.X;
        double cy = bend.Center.Y;

        double minX = Math.Min(bend.StartPoint.X, bend.EndPoint.X);
        double minY = Math.Min(bend.StartPoint.Y, bend.EndPoint.Y);
        double maxX = Math.Max(bend.StartPoint.X, bend.EndPoint.X);
        double maxY = Math.Max(bend.StartPoint.Y, bend.EndPoint.Y);

        foreach (double cardinal in new[] { 0.0, 90.0, 180.0, 270.0 })
        {
            if (!AngleWithinSweep(cardinal, startRadial, span, sign))
                continue;
            double rad = cardinal * Math.PI / 180.0;
            double px = cx + (r * Math.Cos(rad));
            double py = cy + (r * Math.Sin(rad));
            minX = Math.Min(minX, px);
            minY = Math.Min(minY, py);
            maxX = Math.Max(maxX, px);
            maxY = Math.Max(maxY, py);
        }

        return (minX - paddingUm, minY - paddingUm, maxX + paddingUm, maxY + paddingUm);
    }

    /// <summary>True when <paramref name="candidateDegrees"/> lies on the arc's radial sweep.</summary>
    private static bool AngleWithinSweep(double candidateDegrees, double startRadialDegrees,
        double spanDegrees, double sign)
    {
        double delta = ((candidateDegrees - startRadialDegrees) % 360.0 + 360.0) % 360.0;
        if (sign < 0)
            delta = (360.0 - delta) % 360.0;
        return delta <= spanDegrees + 1e-9;
    }
}
