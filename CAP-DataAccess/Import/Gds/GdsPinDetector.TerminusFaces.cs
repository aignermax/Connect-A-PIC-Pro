namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// The terminus-face scan of <see cref="GdsPinDetector"/>: port faces the
/// axis-aligned edge-touch scan cannot see, found directly on the polygon ring
/// (split out to keep the detector below the architecture size limit).
/// </summary>
public static partial class GdsPinDetector
{
    /// <summary>Corner turns at least this sharp (degrees) bracket a terminus-face run;
    /// tessellated arcs turn well under a degree per segment.</summary>
    private const double TerminusCornerMinTurnDegrees = 45.0;

    /// <summary>Terminus faces wider than this (µm) are not waveguide ports (solid-body
    /// edges of label-free cells stay pin-less, as before).</summary>
    private const double TerminusFaceMaxWidthUm = 3.0;

    /// <summary>
    /// Port faces the axis-aligned edge scan cannot see: a partial-angle bend
    /// (gdsfactory fan-outs use 94°/77°/…) ends in a face tilted a few degrees
    /// past every bbox edge — no edge touch, no pin, a broken chain (field
    /// report: rotated fan-out straights never connected through their bends).
    /// A face is explicit on the ring instead: the short run between the two
    /// SHARP corners where the tessellated channel walls meet the face. Runs
    /// are kept when a channel continues inward (same probe as the apex rule)
    /// and deduplicated against pins the label/edge passes already found.
    /// Runs only for cells without label/marker pins: device cells keep their
    /// labeled pin set exactly as before.
    /// </summary>
    private static void AddTerminusFacePins(
        List<Candidate> candidates,
        IReadOnlyList<GdsPolygon> waveguidePolygons,
        GdsBoundingBox cellBBox,
        GdsPinDetectionOptions options)
    {
        foreach (var polygon in waveguidePolygons)
        {
            var ring = DistinctRing(polygon.Points);
            int n = ring.Count;
            if (n < 3)
                continue;

            var sharpIndices = new List<int>();
            for (int i = 0; i < n; i++)
            {
                if (Math.Abs(RingTurnDegrees(ring, i)) >= TerminusCornerMinTurnDegrees)
                    sharpIndices.Add(i);
            }
            if (sharpIndices.Count < 2)
                continue;

            for (int k = 0; k < sharpIndices.Count; k++)
            {
                int from = sharpIndices[k];
                int to = sharpIndices[(k + 1) % sharpIndices.Count];
                var (midpoint, chordStart, chordEnd, width) = RunSummary(ring, from, to);
                if (width < options.MinPinWidthUm || width > TerminusFaceMaxWidthUm)
                    continue;

                double appX = ToAppX(midpoint.X, cellBBox);
                double appY = ToAppY(midpoint.Y, cellBBox);
                if (candidates.Any(c => Math.Abs(c.Pin.XUm - appX) <= options.EdgeTouchToleranceUm
                                     && Math.Abs(c.Pin.YUm - appY) <= options.EdgeTouchToleranceUm))
                    continue; // already found by the label/marker/edge passes

                double outward = SegmentOutwardAngleDegrees(polygon, chordStart, chordEnd);
                if (!FaceChannelContinuesInward(polygon, midpoint, outward, width))
                    continue;

                candidates.Add(new Candidate(NearestEdge(midpoint, cellBBox), new DetectedPin
                {
                    Name = string.Empty,
                    XUm = appX,
                    YUm = appY,
                    AngleDegrees = outward,
                    WidthUm = width,
                    Layer = polygon.Layer,
                    Source = DetectedPinSource.EdgeHeuristic,
                }));
            }
        }
    }

    /// <summary>The turn (degrees, signed) at ring vertex <paramref name="i"/>.</summary>
    private static double RingTurnDegrees(IReadOnlyList<GdsPoint> ring, int i)
    {
        int n = ring.Count;
        var prev = ring[(i + n - 1) % n];
        var curr = ring[i];
        var next = ring[(i + 1) % n];
        double a1 = Math.Atan2(curr.Y - prev.Y, curr.X - prev.X) * 180.0 / Math.PI;
        double a2 = Math.Atan2(next.Y - curr.Y, next.X - curr.X) * 180.0 / Math.PI;
        double turn = (a2 - a1) % 360.0;
        if (turn > 180.0) turn -= 360.0;
        if (turn <= -180.0) turn += 360.0;
        return turn;
    }

    /// <summary>
    /// The polyline run along the ring from sharp vertex <paramref name="from"/> to the
    /// next sharp vertex <paramref name="to"/>: its arc-length midpoint, chord endpoints
    /// and total length.
    /// </summary>
    private static (GdsPoint Midpoint, GdsPoint ChordStart, GdsPoint ChordEnd, double Length) RunSummary(
        IReadOnlyList<GdsPoint> ring, int from, int to)
    {
        int n = ring.Count;
        double length = 0;
        for (int i = from; i != to; i = (i + 1) % n)
            length += Dist(ring[i], ring[(i + 1) % n]);

        double half = length / 2.0;
        double walked = 0;
        var midpoint = ring[from];
        for (int i = from; i != to; i = (i + 1) % n)
        {
            double step = Dist(ring[i], ring[(i + 1) % n]);
            if (walked + step >= half && step > 0)
            {
                double f = (half - walked) / step;
                midpoint = new GdsPoint(
                    ring[i].X + (ring[(i + 1) % n].X - ring[i].X) * f,
                    ring[i].Y + (ring[(i + 1) % n].Y - ring[i].Y) * f);
                break;
            }
            walked += step;
        }
        return (midpoint, ring[from], ring[to], length);
    }

    /// <summary>Depth probe for a terminus face: the channel must continue inward from
    /// the face midpoint (same threshold as the arc-apex rule, probed at 90% of it
    /// to stay off the far boundary of exactly-threshold-long channels).</summary>
    private static bool FaceChannelContinuesInward(
        GdsPolygon polygon, GdsPoint faceMidpoint, double outwardAppDegrees, double widthUm)
    {
        // The outward angle arrived in the app convention; the polygon lives in
        // GDS space (Y-up) — flip Y back: gds direction = (cos θ, −sin θ).
        double rad = outwardAppDegrees * Math.PI / 180.0;
        double inwardX = -Math.Cos(rad);
        double inwardY = Math.Sin(rad);
        double depth = Math.Max(2.0 * widthUm, 1.0) * 0.9;
        var probe = new GdsPoint(faceMidpoint.X + inwardX * depth, faceMidpoint.Y + inwardY * depth);
        return PointInPolygon(polygon.Points, probe);
    }

    private static double Dist(GdsPoint a, GdsPoint b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>The ring without the GDS closing repeat and without consecutive duplicates.</summary>
    private static List<GdsPoint> DistinctRing(IReadOnlyList<GdsPoint> points)
    {
        var ring = new List<GdsPoint>(points.Count);
        foreach (var point in points)
        {
            if (ring.Count == 0 || !ring[^1].Equals(point))
                ring.Add(point);
        }
        if (ring.Count > 1 && ring[0].Equals(ring[^1]))
            ring.RemoveAt(ring.Count - 1);
        return ring;
    }
}
