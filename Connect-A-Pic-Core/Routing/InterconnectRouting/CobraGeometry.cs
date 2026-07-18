namespace CAP_Core.Routing.InterconnectRouting;

/// <summary>
/// Builds the cobra curve an <c>nd.cobra(xya=…)</c> produces, approximated as a cubic Hermite
/// polynomial that satisfies position AND tangent direction at BOTH ends:
/// <c>P(t) = h00·P0 + h10·T0 + h01·P1 + h11·T1</c> with the Hermite basis
/// <c>h00 = 2t³−3t²+1, h10 = t³−2t²+t, h01 = −2t³+3t², h11 = t³−t²</c>,
/// where the end tangents <c>T0/T1</c> point along the start heading and the arrival
/// heading, scaled by the chord length (the standard Hermite magnitude choice).
///
/// Nazca's cobra is likewise a polynomial spline that interpolates (x, y, angle) at both
/// ends; the Hermite form shares that contract, so the canvas curve is a faithful stand-in
/// for the exported primitive. Sampled into <see cref="SampleCount"/> straight chords
/// (see <see cref="CurvePolyline"/>).
/// </summary>
public static class CobraGeometry
{
    /// <summary>Number of straight chords the cobra curve is sampled into (matches
    /// <see cref="SineBendGeometry.SampleCount"/> for a consistent canvas smoothness).</summary>
    public const int SampleCount = 48;

    private const double DegreesToRadians = Math.PI / 180.0;
    private const double Epsilon = 1e-6;

    /// <summary>
    /// Builds the cobra polyline in app-space, or null when the pins (nearly) coincide —
    /// there is no chord to span, so the caller falls back.
    /// </summary>
    /// <param name="startX">Start pin X in app-space micrometers.</param>
    /// <param name="startY">Start pin Y in app-space micrometers.</param>
    /// <param name="startAngleDegrees">Start pin heading in degrees.</param>
    /// <param name="endX">End pin X in app-space micrometers.</param>
    /// <param name="endY">End pin Y in app-space micrometers.</param>
    /// <param name="arrivalAngleDegrees">Heading the curve arrives INTO the end pin with
    /// (the end pin's outward angle rotated by 180°).</param>
    /// <returns>The polyline segments, or null when the pins coincide.</returns>
    public static IReadOnlyList<PathSegment>? Build(
        double startX, double startY, double startAngleDegrees,
        double endX, double endY, double arrivalAngleDegrees)
    {
        double chordX = endX - startX;
        double chordY = endY - startY;
        double chord = Math.Sqrt(chordX * chordX + chordY * chordY);
        if (chord <= Epsilon)
            return null;

        double startRad = startAngleDegrees * DegreesToRadians;
        double arrivalRad = arrivalAngleDegrees * DegreesToRadians;
        double t0X = chord * Math.Cos(startRad);
        double t0Y = chord * Math.Sin(startRad);
        double t1X = chord * Math.Cos(arrivalRad);
        double t1Y = chord * Math.Sin(arrivalRad);

        var points = new List<(double X, double Y)>(SampleCount + 1);
        for (int i = 0; i <= SampleCount; i++)
        {
            double t = (double)i / SampleCount;
            double t2 = t * t;
            double t3 = t2 * t;
            double h00 = 2 * t3 - 3 * t2 + 1;
            double h10 = t3 - 2 * t2 + t;
            double h01 = -2 * t3 + 3 * t2;
            double h11 = t3 - t2;
            points.Add((h00 * startX + h10 * t0X + h01 * endX + h11 * t1X,
                        h00 * startY + h10 * t0Y + h01 * endY + h11 * t1Y));
        }

        return CurvePolyline.ToSegments(points);
    }
}
