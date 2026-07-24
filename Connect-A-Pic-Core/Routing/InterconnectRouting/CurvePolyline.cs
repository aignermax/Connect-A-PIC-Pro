namespace CAP_Core.Routing.InterconnectRouting;

/// <summary>
/// Turns a list of sampled curve points into a renderable polyline of
/// <see cref="StraightSegment"/>s. Shared by the smooth-curve builders
/// (<see cref="SineBendGeometry"/>, <see cref="CobraGeometry"/>).
///
/// The polyline deliberately contains NO <see cref="BendSegment"/>s: a smooth analytic curve
/// has no single circular radius, so there is nothing for the in-canvas radius handles to
/// grab — <c>BendRadiusEditor.GetBendCorners</c> correctly returns no handles for it.
/// </summary>
public static class CurvePolyline
{
    /// <summary>Chords shorter than this are merged into their neighbor (guards against
    /// zero-length segments from duplicate samples).</summary>
    private const double MinChordMicrometers = 1e-9;

    /// <summary>
    /// Builds consecutive <see cref="StraightSegment"/>s through the sample points.
    /// Non-finite points (NaN/Infinity) or fewer than two distinct points yield null,
    /// so callers can fall back instead of producing corrupt geometry.
    /// </summary>
    /// <param name="points">Curve samples in app-space micrometers, in path order.</param>
    /// <returns>The polyline segments, or null when the samples are degenerate.</returns>
    public static IReadOnlyList<PathSegment>? ToSegments(IReadOnlyList<(double X, double Y)> points)
    {
        if (points == null || points.Count < 2)
            return null;
        if (points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y)))
            return null;

        var segments = new List<PathSegment>();
        var (previousX, previousY) = points[0];
        for (int i = 1; i < points.Count; i++)
        {
            var (x, y) = points[i];
            double dx = x - previousX;
            double dy = y - previousY;
            if (Math.Sqrt(dx * dx + dy * dy) <= MinChordMicrometers)
                continue;

            double chordAngle = Math.Atan2(dy, dx) * 180.0 / Math.PI;
            segments.Add(new StraightSegment(previousX, previousY, x, y, chordAngle));
            (previousX, previousY) = (x, y);
        }

        return segments.Count > 0 ? segments : null;
    }
}
