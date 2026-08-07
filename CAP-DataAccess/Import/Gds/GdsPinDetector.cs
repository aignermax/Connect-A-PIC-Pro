namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// Detects component pins on a flattened GDS cell and reports them in the
/// application's coordinate convention, so callers never see GDS orientation.
///
/// Coordinate mapping (applied to every emitted pin):
/// <list type="bullet">
/// <item>GDS space: micrometers, Y-up. App space: micrometers, Y-down, origin at
/// the top-left corner of the cell bounding box:
/// <c>appX = gdsX − bbox.MinX</c>, <c>appY = bbox.MaxY − gdsY</c>.</item>
/// <item>App pin angles follow the direction (cos θ, sin θ) in the Y-down app
/// plane (matching how the app renders and exports pin angles): 0° = east
/// (outward on the right edge), 90° = down (outward on the bottom edge),
/// 180° = west (left edge), 270° = up (top edge). The Y-flip means the visual
/// top edge is the GDS <c>MaxY</c> line and the visual bottom edge is <c>MinY</c>.</item>
/// </list>
///
/// Two detection strategies run over the same cell:
/// <list type="number">
/// <item>Label pins: every TEXT on a configured port layer becomes a named pin at
/// its anchor. The angle is the outward normal of the nearest waveguide/metal
/// polygon SEGMENT when the anchor lies on such a polygon (within
/// <see cref="GdsPinDetectionOptions.LabelGeometryTouchToleranceUm"/> of its
/// outline) — the local geometry says where the pin points, which stays correct
/// for black-box cells whose labels sit deep inside the bounding box; labels
/// with no polygon near fall back to the outward normal of the bounding-box
/// edge nearest to the anchor. The pin KIND is inferred the same way: an anchor
/// touching a metal-layer polygon (its outline, or its interior) is electrical;
/// one touching only waveguide polygons stays kind-unknown rather than
/// proven-optical, so a later metal-route match can still classify the pin
/// electrical; with no polygon near, the label text decides
/// (<see cref="ElectricalLabelMarkers"/>) — anything else stays kind-unknown
/// (the optical default downstream).</item>
/// <item>Edge heuristic: waveguide-layer polygon segments lying on a bounding-box
/// edge line yield a pin at the segment midpoint with the segment length as
/// width. Touches already covered by a label pin are suppressed, and adjacent
/// touches on the same edge are merged.</item>
/// </list>
/// The result is ordered deterministically by edge (left, top, right, bottom)
/// and then by position along the edge; heuristic pins are named
/// <c>heur_1..N</c> in that final order.
/// </summary>
public static partial class GdsPinDetector
{
    /// <summary>
    /// A bounding-box edge, in the deterministic output order. Top/bottom are
    /// visual (app-space) edges: Top is the GDS <c>MaxY</c> line, Bottom the
    /// GDS <c>MinY</c> line.
    /// </summary>
    private enum CellEdge
    {
        Left = 0,
        Top = 1,
        Right = 2,
        Bottom = 3,
    }

    /// <summary>A pin candidate plus the edge it sits on (needed for deterministic ordering).</summary>
    private readonly record struct Candidate(CellEdge Edge, DetectedPin Pin);

    /// <summary>
    /// Detects pins on <paramref name="flattened"/>. The bounding box is supplied
    /// by the caller (typically <see cref="GdsCellFlattener.GetBoundingBox"/>) and
    /// is used as-is — it defines both the app-space origin and the edges pins
    /// are matched against. An empty cell or a degenerate (zero-area) box yields
    /// an empty list.
    /// </summary>
    public static IReadOnlyList<DetectedPin> Detect(
        FlattenedGdsCell flattened,
        GdsBoundingBox cellBBox,
        GdsPinDetectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(flattened);
        options ??= new GdsPinDetectionOptions();
        // Standalone detection has no library context to resolve the AUTO
        // (null) electrical layers against the own-export sentinel; the Lunima
        // defaults keep single-cell detection behaving as before. The
        // hierarchy importer resolves BEFORE calling in (foreign file → none).
        if (options.ElectricalLayers is null)
            options = options with { ElectricalLayers = GdsPinDetectionOptions.LunimaElectricalLayers };
        options.Validate();

        var result = new List<DetectedPin>();
        if (cellBBox.Width <= 0 || cellBBox.Height <= 0)
            return result;

        double tolerance = options.EdgeTouchToleranceUm;

        // ── 1. Label pins ────────────────────────────────────────────────────
        var labelAnchors = new List<GdsPoint>();
        var candidates = new List<Candidate>();
        AnchorGeometryIndex? geometryIndex = null;
        foreach (var text in flattened.Texts)
        {
            if (!ContainsLayer(options.PortLayers, text.Layer, text.TextType))
                continue;

            // Built once per run, on the first port label — cells without port
            // labels never pay for the spatial index.
            geometryIndex ??= BuildAnchorGeometryIndex(flattened.Polygons, options);
            CellEdge edge = NearestEdge(text.Position, cellBBox);
            var geometry = ProbeAnchorGeometry(text.Position, geometryIndex, options);
            labelAnchors.Add(text.Position);
            candidates.Add(new Candidate(edge, new DetectedPin
            {
                Name = text.Text,
                XUm = ToAppX(text.Position.X, cellBBox),
                YUm = ToAppY(text.Position.Y, cellBBox),
                AngleDegrees = geometry is { Polygon: { } directionPolygon }
                    ? SegmentOutwardAngleDegrees(directionPolygon, geometry.P1, geometry.P2)
                    : OutwardAngleDegrees(edge),
                WidthUm = 0,
                Source = DetectedPinSource.Label,
                IsElectrical = InferLabelPinKind(text.Text, geometry),
            }));
        }

        // ── 2. Edge heuristic ────────────────────────────────────────────────
        // Collect touch intervals per edge first so overlapping/adjacent touches
        // merge into a single pin.
        var touches = new SortedList<CellEdge, List<(double Start, double End)>>();
        foreach (var polygon in flattened.Polygons)
        {
            if (!ContainsLayer(options.WaveguideLayers, polygon.Layer, polygon.DataType))
                continue;

            foreach (var (p1, p2) in Segments(polygon))
            {
                CellEdge? edge = TouchingEdge(p1, p2, cellBBox, tolerance);
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

        foreach (var (edge, intervals) in touches)
        {
            foreach (var (start, end) in MergeIntervals(intervals, tolerance))
            {
                double width = end - start;
                if (width < options.MinPinWidthUm || width > options.MaxPinWidthUm)
                    continue;

                GdsPoint midpoint = MidpointOnEdge(edge, (start + end) / 2.0, cellBBox);
                if (IsCoveredByLabel(midpoint, labelAnchors, tolerance))
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

        // ── 3. Deterministic order + heuristic naming ────────────────────────
        int heuristicCount = 0;
        foreach (var candidate in candidates
            .OrderBy(c => (int)c.Edge)
            .ThenBy(c => c.Edge is CellEdge.Left or CellEdge.Right ? c.Pin.YUm : c.Pin.XUm))
        {
            var pin = candidate.Pin;
            if (pin.Source == DetectedPinSource.EdgeHeuristic)
                pin = pin with { Name = $"heur_{++heuristicCount}" };
            result.Add(pin);
        }

        return result;
    }

    // ── Edge helpers ─────────────────────────────────────────────────────────

    /// <summary>App-space X: 0 at the left edge of the bounding box.</summary>
    private static double ToAppX(double gdsX, GdsBoundingBox bbox) => gdsX - bbox.MinX;

    /// <summary>App-space Y: 0 at the TOP edge (GDS MaxY), growing downward.</summary>
    private static double ToAppY(double gdsY, GdsBoundingBox bbox) => bbox.MaxY - gdsY;

    /// <summary>
    /// Outward normal of a bounding-box edge in the app angle convention
    /// (0° = east, 90° = down, 180° = west, 270° = up in the Y-down plane).
    /// </summary>
    private static double OutwardAngleDegrees(CellEdge edge) => edge switch
    {
        CellEdge.Left => 180.0,
        CellEdge.Top => 270.0,
        CellEdge.Right => 0.0,
        CellEdge.Bottom => 90.0,
        _ => throw new ArgumentOutOfRangeException(nameof(edge), edge, "Unknown cell edge."),
    };

    /// <summary>
    /// The bounding-box edge nearest to <paramref name="point"/>. Ties resolve in
    /// <see cref="CellEdge"/> declaration order (left, top, right, bottom).
    /// </summary>
    private static CellEdge NearestEdge(GdsPoint point, GdsBoundingBox bbox)
    {
        var best = CellEdge.Left;
        double bestDistance = Math.Abs(point.X - bbox.MinX);

        double top = Math.Abs(bbox.MaxY - point.Y);
        if (top < bestDistance) { best = CellEdge.Top; bestDistance = top; }

        double right = Math.Abs(bbox.MaxX - point.X);
        if (right < bestDistance) { best = CellEdge.Right; bestDistance = right; }

        double bottom = Math.Abs(point.Y - bbox.MinY);
        if (bottom < bestDistance) { best = CellEdge.Bottom; }

        return best;
    }

    /// <summary>
    /// Returns the edge whose line both segment endpoints lie on (within
    /// <paramref name="tolerance"/>), or null. Edges are checked in declaration
    /// order, so a degenerate corner segment can match at most one edge.
    /// </summary>
    private static CellEdge? TouchingEdge(GdsPoint p1, GdsPoint p2, GdsBoundingBox bbox, double tolerance)
    {
        if (Math.Abs(p1.X - bbox.MinX) <= tolerance && Math.Abs(p2.X - bbox.MinX) <= tolerance)
            return CellEdge.Left;
        if (Math.Abs(p1.Y - bbox.MaxY) <= tolerance && Math.Abs(p2.Y - bbox.MaxY) <= tolerance)
            return CellEdge.Top;
        if (Math.Abs(p1.X - bbox.MaxX) <= tolerance && Math.Abs(p2.X - bbox.MaxX) <= tolerance)
            return CellEdge.Right;
        if (Math.Abs(p1.Y - bbox.MinY) <= tolerance && Math.Abs(p2.Y - bbox.MinY) <= tolerance)
            return CellEdge.Bottom;
        return null;
    }

    /// <summary>Merges intervals that overlap or are separated by at most <paramref name="tolerance"/>.</summary>
    private static List<(double Start, double End)> MergeIntervals(
        List<(double Start, double End)> intervals, double tolerance)
    {
        intervals.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(double Start, double End)>();
        foreach (var (start, end) in intervals)
        {
            if (merged.Count > 0 && start <= merged[^1].End + tolerance)
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, end));
            else
                merged.Add((start, end));
        }
        return merged;
    }

    /// <summary>Reconstructs the GDS-space midpoint of a touch interval on an edge.</summary>
    private static GdsPoint MidpointOnEdge(CellEdge edge, double along, GdsBoundingBox bbox) => edge switch
    {
        CellEdge.Left => new GdsPoint(bbox.MinX, along),
        CellEdge.Right => new GdsPoint(bbox.MaxX, along),
        CellEdge.Top => new GdsPoint(along, bbox.MaxY),
        CellEdge.Bottom => new GdsPoint(along, bbox.MinY),
        _ => throw new ArgumentOutOfRangeException(nameof(edge), edge, "Unknown cell edge."),
    };

    /// <summary>True when a label anchor lies within tolerance of the touch midpoint.</summary>
    private static bool IsCoveredByLabel(GdsPoint midpoint, List<GdsPoint> labelAnchors, double tolerance)
    {
        foreach (var anchor in labelAnchors)
        {
            double dx = anchor.X - midpoint.X;
            double dy = anchor.Y - midpoint.Y;
            if (dx * dx + dy * dy <= tolerance * tolerance)
                return true;
        }
        return false;
    }

    private static bool ContainsLayer(
        IReadOnlyList<(int Layer, int Datatype)> layers, int layer, int datatype)
    {
        foreach (var (l, d) in layers)
        {
            if (l == layer && d == datatype)
                return true;
        }
        return false;
    }
}
