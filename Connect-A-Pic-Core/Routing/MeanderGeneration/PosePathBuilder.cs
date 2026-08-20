namespace CAP_Core.Routing.MeanderGeneration;

/// <summary>
/// Incrementally appends tangent-continuous straights and arcs to a <see cref="RoutedPath"/>,
/// tracking the current pose. Arcs follow the <see cref="BendSegment"/> convention
/// (positive sweep = counter-clockwise) and are split into pieces of at most 90°,
/// matching the router's output granularity.
/// </summary>
internal sealed class PosePathBuilder
{
    private const double Eps = 1e-9;
    private const double MaxArcPieceDegrees = 90.0;

    private readonly RoutedPath _path;
    private double _x;
    private double _y;
    private double _headingDeg;

    public PosePathBuilder(RoutedPath path, double startX, double startY, double headingDegrees)
    {
        _path = path;
        _x = startX;
        _y = startY;
        _headingDeg = Normalize360(headingDegrees);
    }

    /// <summary>Current pen position.</summary>
    public (double X, double Y) Position => (_x, _y);

    /// <summary>Appends a straight of the given length along the current heading.</summary>
    public void AppendStraight(double lengthMicrometers)
    {
        if (lengthMicrometers <= Eps)
            return;

        double rad = _headingDeg * Math.PI / 180.0;
        double endX = _x + lengthMicrometers * Math.Cos(rad);
        double endY = _y + lengthMicrometers * Math.Sin(rad);
        _path.Segments.Add(new StraightSegment(_x, _y, endX, endY, _headingDeg));
        _x = endX;
        _y = endY;
    }

    /// <summary>Appends an arc starting tangent to the current heading.</summary>
    public void AppendArc(double sweepDegrees, double radiusMicrometers)
    {
        double remaining = sweepDegrees;
        while (Math.Abs(remaining) > Eps)
        {
            double piece = Math.Clamp(remaining, -MaxArcPieceDegrees, MaxArcPieceDegrees);
            AppendArcPiece(piece, radiusMicrometers);
            remaining -= piece;
        }
    }

    private void AppendArcPiece(double sweepDegrees, double radiusMicrometers)
    {
        double sign = Math.Sign(sweepDegrees);
        double centerAngleRad = (_headingDeg + 90.0 * sign) * Math.PI / 180.0;
        var bend = new BendSegment(
            _x + radiusMicrometers * Math.Cos(centerAngleRad),
            _y + radiusMicrometers * Math.Sin(centerAngleRad),
            radiusMicrometers,
            _headingDeg,
            sweepDegrees);
        _path.Segments.Add(bend);
        _x = bend.EndPoint.X;
        _y = bend.EndPoint.Y;
        _headingDeg = Normalize360(_headingDeg + sweepDegrees);
    }

    private static double Normalize360(double angleDeg)
    {
        angleDeg %= 360.0;
        if (angleDeg < 0.0)
            angleDeg += 360.0;
        return angleDeg;
    }
}
