namespace CAP_Core.Routing.InterconnectRouting;

/// <summary>
/// Builds the smooth sine S-bend an <c>nd.sinebend(distance, offset)</c> produces: in the
/// start pin's frame the curve follows
/// <c>y(x) = offset · (x/distance − sin(2π·x/distance) / (2π))</c>,
/// which starts and arrives PARALLEL to the start heading (y' = 0 at both ends) and shifts
/// laterally by exactly <c>offset</c> over the forward run <c>distance</c>.
///
/// The curve is sampled into a polyline of <see cref="SampleCount"/> straight chords
/// (see <see cref="CurvePolyline"/>), so the canvas draws the same curve basis the exporter
/// hands to <c>nd.sinebend</c> — canvas and GDS match up to the chord sampling.
/// </summary>
public static class SineBendGeometry
{
    /// <summary>Number of straight chords the sine curve is sampled into. 48 keeps the
    /// per-chord turn well below 2° for typical S-bends, so the polyline reads as smooth
    /// at canvas zoom levels while staying cheap to render and export.</summary>
    public const int SampleCount = 48;

    private const double DegreesToRadians = Math.PI / 180.0;
    private const double Epsilon = 1e-6;
    private const double TwoPi = 2.0 * Math.PI;

    /// <summary>
    /// Builds the sine S-bend polyline in app-space, or null when the end pin does not lie
    /// ahead of the start pin (the sine parameterization needs a positive forward run).
    /// A negligible lateral offset degenerates to a single exact straight.
    /// </summary>
    /// <param name="startX">Start pin X in app-space micrometers.</param>
    /// <param name="startY">Start pin Y in app-space micrometers.</param>
    /// <param name="startAngleDegrees">Start pin heading in degrees.</param>
    /// <param name="longitudinal">Forward reach along the start heading (µm) — the exporter's
    /// <c>distance</c>. Must be positive.</param>
    /// <param name="lateral">Signed lateral offset perpendicular to the start heading (µm) —
    /// the exporter's <c>offset</c>.</param>
    /// <returns>The polyline segments, or null when the layout is degenerate.</returns>
    public static IReadOnlyList<PathSegment>? Build(
        double startX, double startY, double startAngleDegrees,
        double longitudinal, double lateral)
    {
        if (longitudinal <= Epsilon)
            return null;

        double rad = startAngleDegrees * DegreesToRadians;
        double cos = Math.Cos(rad);
        double sin = Math.Sin(rad);

        if (Math.Abs(lateral) <= Epsilon)
        {
            // Collinear pins: one exact straight instead of dozens of collinear chords.
            var straight = new StraightSegment(
                startX, startY,
                startX + longitudinal * cos, startY + longitudinal * sin,
                startAngleDegrees);
            return new List<PathSegment> { straight };
        }

        var points = new List<(double X, double Y)>(SampleCount + 1);
        for (int i = 0; i <= SampleCount; i++)
        {
            double u = (double)i / SampleCount;
            double localX = longitudinal * u;
            double localY = lateral * (u - Math.Sin(TwoPi * u) / TwoPi);
            points.Add((startX + localX * cos - localY * sin,
                        startY + localX * sin + localY * cos));
        }

        return CurvePolyline.ToSegments(points);
    }
}
