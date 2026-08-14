namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// Edge-heuristic pin detection for <see cref="GdsPinDetector"/>: waveguide
/// polygon segments lying on the reference-frame boundary become pins
/// (strategy 2), plus the terminus-face ring scan (2b) and the
/// degenerate-channel fallback for label-free stub cells. Split out to keep
/// the orchestrating file within the architecture size limit.
/// </summary>
public static partial class GdsPinDetector
{
    /// <summary>Ports at most this wide must prove an inward channel (the arc-apex rule);
    /// wider touches are accepted without the probe.</summary>
    private const double ApexProbeMaxPortWidthUm = 2.0;

    /// <summary>
    /// Adds edge-heuristic pins to <paramref name="candidates"/>.
    /// Label-free cells (route cells: straights, bends, sine bends) scan
    /// against the WAVEGUIDE GEOMETRY's own extent, not the cell bbox:
    /// envelope/marker polygons (e.g. gdsfactory's layer-68 bend envelope)
    /// routinely inflate the cell bbox a few hundred nm past the core's
    /// port faces, which would push those faces beyond the touch tolerance
    /// and silently drop the pins there (field report: bend cells kept only
    /// their west pin, breaking every chain joint at the bend exit). Cells
    /// WITH label/marker pins keep the full-bbox frame — their labeled pins
    /// are authoritative and the heuristic only fills gaps near them.
    /// Pin coordinates are always emitted in the full cell-bbox frame.
    /// </summary>
    private static void AddEdgeHeuristicPins(
        List<Candidate> candidates,
        List<GdsPoint> labelAnchors,
        FlattenedGdsCell flattened,
        GdsBoundingBox cellBBox,
        GdsPinDetectionOptions options)
    {
        double tolerance = options.EdgeTouchToleranceUm;
        bool labelFree = !candidates.Any(
            c => c.Pin.Source is DetectedPinSource.Label or DetectedPinSource.ArrowMarker or DetectedPinSource.PortShape);
        var waveguidePolygons = flattened.Polygons
            .Where(p => ContainsLayer(options.WaveguideLayers, p.Layer, p.DataType))
            .ToList();
        var edgeFrame = cellBBox;
        if (labelFree && waveguidePolygons.Count > 0)
        {
            edgeFrame = new GdsBoundingBox(
                waveguidePolygons.Min(p => p.Points.Min(pt => pt.X)),
                waveguidePolygons.Min(p => p.Points.Min(pt => pt.Y)),
                waveguidePolygons.Max(p => p.Points.Max(pt => pt.X)),
                waveguidePolygons.Max(p => p.Points.Max(pt => pt.Y)));
        }

        // Collect touch intervals per edge first so overlapping/adjacent
        // touches merge into a single pin.
        var touches = CollectEdgeTouches(waveguidePolygons, edgeFrame, tolerance);
        foreach (var (edge, intervals) in touches)
        {
            // Sidewall rule (waveguide-frame scans only): a touch interval
            // longer than the frame's PERPENDICULAR extent is the waveguide's
            // long SIDE lying on the reference edge, not a port face (the
            // waveguide frame pulls envelope margins inward, which would
            // otherwise expose those sides as phantom pins). A port face is
            // never wider than the waveguide is deep.
            double perpendicularExtent = edge is CellEdge.Left or CellEdge.Right
                ? edgeFrame.Width
                : edgeFrame.Height;
            foreach (var (start, end) in MergeIntervals(intervals, tolerance))
            {
                double width = end - start;
                if (width < options.MinPinWidthUm || width > options.MaxPinWidthUm)
                    continue;
                if (labelFree && width > perpendicularExtent + tolerance)
                    continue;

                GdsPoint midpoint = MidpointOnEdge(edge, (start + end) / 2.0, edgeFrame);
                if (IsCoveredByLabel(midpoint, labelAnchors, tolerance))
                    continue;
                // Arc-apex rule (waveguide-frame scans only): a port FACE has
                // the waveguide channel continuing inward; a tessellated curve
                // merely grazes the reference edge with the thin annulus wall
                // behind it. One decisive probe point separates them (wide
                // ports skip the probe: nothing apex-like is that wide, and
                // deeply probed wide tapers would false-fire).
                if (labelFree && width <= ApexProbeMaxPortWidthUm
                    && !ChannelContinuesInward(midpoint, edge, width, waveguidePolygons))
                    continue;

                candidates.Add(new Candidate(edge, new DetectedPin
                {
                    Name = string.Empty,
                    XUm = ToAppX(midpoint.X, cellBBox),
                    YUm = ToAppY(midpoint.Y, cellBBox),
                    AngleDegrees = OutwardAngleDegrees(edge),
                    WidthUm = width,
                    Source = DetectedPinSource.EdgeHeuristic,
                }));
            }
        }

        // Terminus faces at any exit angle: device cells keep their labeled
        // pin set; only label-free route cells get the ring scan.
        if (labelFree)
            AddTerminusFacePins(candidates, waveguidePolygons, cellBBox, options);

        if (labelFree && touches.Count > 0
            && !candidates.Any(c => c.Pin.Source == DetectedPinSource.EdgeHeuristic))
        {
            AddDegenerateChannelFallbackPins(candidates, touches, edgeFrame, cellBBox, options);
        }
    }

    /// <summary>Waveguide-polygon touch intervals grouped by the reference-frame edge they lie on.</summary>
    private static SortedList<CellEdge, List<(double Start, double End)>> CollectEdgeTouches(
        IReadOnlyList<GdsPolygon> waveguidePolygons, GdsBoundingBox edgeFrame, double tolerance)
    {
        var touches = new SortedList<CellEdge, List<(double Start, double End)>>();
        foreach (var polygon in waveguidePolygons)
        {
            foreach (var (p1, p2) in Segments(polygon))
            {
                CellEdge? edge = TouchingEdge(p1, p2, edgeFrame, tolerance);
                if (edge is null)
                    continue;

                (double start, double end) = edge is CellEdge.Left or CellEdge.Right
                    ? (Math.Min(p1.Y, p2.Y), Math.Max(p1.Y, p2.Y))
                    : (Math.Min(p1.X, p2.X), Math.Max(p1.X, p2.X));
                if (!touches.TryGetValue(edge.Value, out var list))
                    touches.Add(edge.Value, list = new List<(double, double)>());
                list.Add((start, end));
            }
        }
        return touches;
    }

    /// <summary>
    /// Degenerate-channel fallback: the sidewall/apex rules exist to REMOVE
    /// spurious pins — when every rule path (edge scan AND terminus scan)
    /// comes up empty on a label-free cell that HAS touches (a gdsfactory
    /// stub straight shorter than its own width fails all of them), the
    /// conservative answer is the plain width-window acceptance, exactly the
    /// pre-rule behavior: dangling extra pins are harmless, a silently
    /// broken chain is not. Sidewall-looking intervals stay rejected unless
    /// the frame is sub-µm along their edge (there the aspect comparison is
    /// meaningless — a stub slice's propagation runs along its SHORT axis).
    /// </summary>
    private static void AddDegenerateChannelFallbackPins(
        List<Candidate> candidates,
        SortedList<CellEdge, List<(double Start, double End)>> touches,
        GdsBoundingBox edgeFrame,
        GdsBoundingBox cellBBox,
        GdsPinDetectionOptions options)
    {
        double tolerance = options.EdgeTouchToleranceUm;
        foreach (var (edge, intervals) in touches)
        {
            double perpendicularExtent = edge is CellEdge.Left or CellEdge.Right
                ? edgeFrame.Width
                : edgeFrame.Height;
            double alongExtent = edge is CellEdge.Left or CellEdge.Right
                ? edgeFrame.Height
                : edgeFrame.Width;
            foreach (var (start, end) in MergeIntervals(intervals, tolerance))
            {
                double width = end - start;
                if (width < options.MinPinWidthUm || width > options.MaxPinWidthUm)
                    continue;
                if (width > perpendicularExtent + tolerance && alongExtent > 1.0)
                    continue;

                GdsPoint midpoint = MidpointOnEdge(edge, (start + end) / 2.0, edgeFrame);
                candidates.Add(new Candidate(edge, new DetectedPin
                {
                    Name = string.Empty,
                    XUm = ToAppX(midpoint.X, cellBBox),
                    YUm = ToAppY(midpoint.Y, cellBBox),
                    AngleDegrees = OutwardAngleDegrees(edge),
                    WidthUm = width,
                    Source = DetectedPinSource.EdgeHeuristic,
                }));
            }
        }
    }

    /// <summary>
    /// True when the waveguide channel continues inward from a touch midpoint:
    /// a single probe point along the edge's inward normal must still lie inside
    /// a waveguide polygon. The probe sits at 90% of
    /// <c>max(2 × port width, 1 µm)</c> — the bias keeps the point off the far
    /// boundary when the channel is exactly threshold-long (even-odd is
    /// implementation-defined ON an edge). Port faces pass (the channel runs
    /// on), arc-apex grazes fail (only the thin annulus wall lies behind the
    /// extreme point).
    /// </summary>
    private static bool ChannelContinuesInward(
        GdsPoint midpoint, CellEdge edge, double portWidthUm, IReadOnlyList<GdsPolygon> waveguidePolygons)
    {
        double depth = Math.Max(2.0 * portWidthUm, 1.0) * 0.9;
        var (dx, dy) = edge switch
        {
            CellEdge.Left => (1.0, 0.0),
            CellEdge.Right => (-1.0, 0.0),
            CellEdge.Top => (0.0, -1.0),   // GDS Y-up: top edge's inward is −Y
            CellEdge.Bottom => (0.0, 1.0),
            _ => throw new ArgumentOutOfRangeException(nameof(edge), edge, "Unknown cell edge."),
        };
        var probe = new GdsPoint(midpoint.X + dx * depth, midpoint.Y + dy * depth);
        return waveguidePolygons.Any(p => PointInPolygon(p.Points, probe));
    }
}
