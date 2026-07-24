namespace CAP_Core.Routing.InterconnectRouting;

/// <summary>
/// Pure geometry for mapping between a bend's radius and the screen position of its
/// radius handle. Kept UI-free so the drag mapping can be unit-tested without Avalonia.
/// </summary>
public static class BendHandleGeometry
{
    /// <summary>
    /// World-space point at which the handle for <paramref name="corner"/> is drawn:
    /// <c>Corner + Radius · HandleFactor · Bisector</c> (the arc's point nearest the corner).
    /// </summary>
    public static (double X, double Y) HandlePoint(BendCorner corner)
    {
        double d = corner.RadiusMicrometers * corner.HandleFactor;
        return (corner.Corner.X + d * corner.Bisector.X,
                corner.Corner.Y + d * corner.Bisector.Y);
    }

    /// <summary>
    /// Signed distance of <paramref name="pointer"/> from <paramref name="corner"/> along the
    /// (unit) <paramref name="bisector"/> — the pointer projected onto the handle's slide ray.
    /// </summary>
    public static double ProjectDistance((double X, double Y) corner,
                                         (double X, double Y) bisector,
                                         (double X, double Y) pointer)
    {
        double dx = pointer.X - corner.X;
        double dy = pointer.Y - corner.Y;
        return dx * bisector.X + dy * bisector.Y;
    }

    /// <summary>
    /// Inverse of the handle placement: the radius whose handle sits at
    /// <paramref name="distance"/> along the bisector, given the bend's
    /// <paramref name="handleFactor"/>. Returns 0 when the factor is degenerate.
    /// </summary>
    public static double RadiusFromDistance(double distance, double handleFactor)
    {
        const double epsilon = 1e-9;
        return Math.Abs(handleFactor) < epsilon ? 0.0 : distance / handleFactor;
    }
}
