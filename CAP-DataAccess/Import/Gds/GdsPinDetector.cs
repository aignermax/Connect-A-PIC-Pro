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
/// Three detection strategies run over the same cell:
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
/// (the optical default downstream). When a label sits on an explicit port-layer
/// polygon shape that touches the cell boundary, the label adopts the shape's
/// exact boundary midpoint and width.</item>
/// <item>Port-layer shapes: polygons drawn on configured port layers that touch
/// the cell boundary are themselves port markers. Each boundary touch becomes a
/// pin at the touch midpoint, with the touch length as width and the edge's
/// outward normal as direction. Shapes already covered by a label are consumed
/// by that label (supplying width/position); uncovered shapes become
/// <see cref="DetectedPinSource.PortShape"/> pins named in the heuristic
/// sequence.</item>
/// <item>Edge heuristic: waveguide-layer polygon segments lying on a bounding-box
/// edge line yield a pin at the segment midpoint with the segment length as
/// width. Touches already covered by a label pin are suppressed, and adjacent
/// touches on the same edge are merged.</item>
/// </list>
/// The result is ordered deterministically by edge (left, top, right, bottom)
/// and then by position along the edge; unnamed pins (edge heuristic, arrow
/// markers, and uncovered port-layer shapes) are named <c>heur_1..N</c> in that
/// final order.
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
        options.Validate();

        var result = new List<DetectedPin>();
        if (cellBBox.Width <= 0 || cellBBox.Height <= 0)
            return result;

        double tolerance = options.EdgeTouchToleranceUm;

        // ── 0. Port-layer shape candidates ───────────────────────────────────
        // Explicit port shapes are detected first: they give a label at the
        // same location its width and exact boundary position, and any shape
        // not named by a label becomes a pin of its own.
        var portShapes = FindPortLayerShapeCandidates(flattened, cellBBox, options);
        var coveredPortShapes = new HashSet<int>();

        // ── 1. Label pins ────────────────────────────────────────────────────
        var labelAnchors = new List<GdsPoint>();
        var candidates = new List<Candidate>();
        AnchorGeometryIndex? geometryIndex = null;
        // nazca-style arrow markers carry exact pin positions for this cell.
        var markers = GdsPinArrowMarkers.Find(flattened, cellBBox);
        foreach (var text in flattened.Texts)
        {
            if (!ContainsLayer(options.PortLayers, text.Layer, text.TextType))
                continue;
            // nazca placement anchors / parameter annotations are never ports —
            // filtered on every path, configured port layers included.
            if (GdsGhostLabelFilter.IsGhost(text))
                continue;

            // Built once per run, on the first port label — cells without port
            // labels never pay for the spatial index.
            geometryIndex ??= BuildAnchorGeometryIndex(flattened.Polygons, options);
            // An arrow marker near the label holds the exact pin position:
            // re-anchor to its tip so direction and material are probed where
            // the pin really is, not where the label happened to be placed.
            var anchor = NearestMarkerTip(text.Position, markers, MarkerSnapToleranceUm) ?? text.Position;
            // A port-layer shape at the boundary gives the exact pin position
            // and width; arrow markers take precedence for position, but the
            // shape still contributes width when the label sits on it.
            var portShape = NearestPortShape(text.Position, portShapes, MarkerSnapToleranceUm);
            double portShapeWidth = 0;
            if (portShape is not null)
            {
                portShapeWidth = portShape.Value.Candidate.WidthUm;
                if (!markers.Any(m => DistanceSquaredLessOrEqual(m.Position, text.Position, MarkerSnapToleranceUm)))
                {
                    anchor = portShape.Value.Candidate.Midpoint;
                }
                coveredPortShapes.Add(portShape.Value.Index);
            }
            CellEdge edge = NearestEdge(anchor, cellBBox);
            var geometry = ProbeAnchorGeometry(anchor, geometryIndex, options);
            labelAnchors.Add(anchor);
            candidates.Add(new Candidate(edge, new DetectedPin
            {
                Name = text.Text,
                XUm = ToAppX(anchor.X, cellBBox),
                YUm = ToAppY(anchor.Y, cellBBox),
                AngleDegrees = geometry is { Polygon: { } directionPolygon }
                    ? SegmentOutwardAngleDegrees(directionPolygon, geometry.P1, geometry.P2)
                    : OutwardAngleDegrees(edge),
                WidthUm = portShapeWidth,
                Source = DetectedPinSource.Label,
                IsElectrical = InferLabelPinKind(text.Text, geometry),
            }));
        }

        // ── 1b. Arrow-marker pins ────────────────────────────────────────────
        // Markers without a nearby label still yield a pin — but only when
        // route geometry lies at the tip: floating orientation chevrons (they
        // merely indicate "outside" for an edge) never become pins.
        if (markers.Count > 0)
        {
            geometryIndex ??= BuildAnchorGeometryIndex(flattened.Polygons, options);
            foreach (var marker in markers)
            {
                if (IsCoveredByLabel(marker.Position, labelAnchors, MarkerSnapToleranceUm))
                    continue;
                var geometry = ProbeAnchorGeometry(marker.Position, geometryIndex, options);
                if (geometry is null)
                    continue;
                CellEdge edge = NearestEdge(marker.Position, cellBBox);
                labelAnchors.Add(marker.Position);
                candidates.Add(new Candidate(edge, new DetectedPin
                {
                    Name = string.Empty,
                    XUm = ToAppX(marker.Position.X, cellBBox),
                    YUm = ToAppY(marker.Position.Y, cellBBox),
                    AngleDegrees = geometry is { Polygon: { } markerPolygon }
                        ? SegmentOutwardAngleDegrees(markerPolygon, geometry.P1, geometry.P2)
                        : OutwardAngleDegrees(edge),
                    WidthUm = 0,
                    Source = DetectedPinSource.ArrowMarker,
                    IsElectrical = InferLabelPinKind(string.Empty, geometry),
                }));
            }
        }

        // ── 1c. Port-layer shapes not named by labels ────────────────────────
        AddPortLayerShapePins(candidates, labelAnchors, portShapes, coveredPortShapes, cellBBox, options);

        // ── 2. Edge heuristic ────────────────────────────────────────────────
        // Label-free cells (route cells: straights, bends, sine bends) scan
        // against the WAVEGUIDE GEOMETRY's own extent, not the cell bbox:
        // envelope/marker polygons (e.g. gdsfactory's layer-68 bend envelope)
        // routinely inflate the cell bbox a few hundred nm past the core's
        // port faces, which would push those faces beyond the touch tolerance
        // and silently drop the pins there (field report: bend cells kept only
        // their west pin, breaking every chain joint at the bend exit). Cells
        // WITH label/marker pins keep the full-bbox frame — their labeled pins
        // are authoritative and the heuristic only fills gaps near them.
        // Pin coordinates are always emitted in the full cell-bbox frame.
        // Collect touch intervals per edge first so overlapping/adjacent touches
        // merge into a single pin.
        bool labelFree = !candidates.Any(
            c => c.Pin.Source is DetectedPinSource.Label or DetectedPinSource.ArrowMarker or DetectedPinSource.PortShape);
        var waveguidePolygons = flattened.Polygons
            .Where(p => ContainsLayer(options.WaveguideLayers, p.Layer, p.DataType))
            .ToList();
        var touches = new SortedList<CellEdge, List<(double Start, double End)>>();
        var edgeFrame = cellBBox;
        if (labelFree && waveguidePolygons.Count > 0)
        {
            edgeFrame = new GdsBoundingBox(
                waveguidePolygons.Min(p => p.Points.Min(pt => pt.X)),
                waveguidePolygons.Min(p => p.Points.Min(pt => pt.Y)),
                waveguidePolygons.Max(p => p.Points.Max(pt => pt.X)),
                waveguidePolygons.Max(p => p.Points.Max(pt => pt.Y)));
        }
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

        // ── 2b. Terminus faces at any exit angle ─────────────────────────────
        // Device cells keep their labeled pin set; only label-free route cells
        // get the ring scan.
        if (labelFree)
            AddTerminusFacePins(candidates, waveguidePolygons, cellBBox, options);

        // Degenerate-channel fallback: the sidewall/apex rules exist to REMOVE
        // spurious pins — when every rule path (edge scan AND terminus scan)
        // comes up empty on a label-free cell that HAS touches (a gdsfactory
        // stub straight shorter than its own width fails all of them), the
        // conservative answer is the plain width-window acceptance, exactly the
        // pre-rule behavior: dangling extra pins are harmless, a silently
        // broken chain is not. Sidewall-looking intervals stay rejected unless
        // the frame is sub-µm along their edge (there the aspect comparison is
        // meaningless — a stub slice's propagation runs along its SHORT axis).
        if (labelFree && touches.Count > 0
            && !candidates.Any(c => c.Pin.Source == DetectedPinSource.EdgeHeuristic))
        {
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

        // ── 3. Deterministic order + heuristic naming ────────────────────────
        int heuristicCount = 0;
        foreach (var candidate in candidates
            .OrderBy(c => (int)c.Edge)
            .ThenBy(c => c.Edge is CellEdge.Left or CellEdge.Right ? c.Pin.YUm : c.Pin.XUm))
        {
            var pin = candidate.Pin;
            if (pin.Source is DetectedPinSource.EdgeHeuristic or DetectedPinSource.ArrowMarker or DetectedPinSource.PortShape)
                pin = pin with { Name = $"heur_{++heuristicCount}" };
            result.Add(pin);
        }

        return result;
    }

    /// <summary>How far a label anchor may sit from an arrow-marker tip to adopt its exact position.</summary>
    private const double MarkerSnapToleranceUm = 2.0;

    /// <summary>Ports at most this wide must prove an inward channel (the arc-apex rule);
    /// wider touches are accepted without the probe.</summary>
    private const double ApexProbeMaxPortWidthUm = 2.0;

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

    /// <summary>Squared distance between two points, compared against <paramref name="toleranceUm"/>².</summary>
    private static bool DistanceSquaredLessOrEqual(GdsPoint a, GdsPoint b, double toleranceUm)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy <= toleranceUm * toleranceUm;
    }

    /// <summary>The nearest arrow-marker tip within <paramref name="toleranceUm"/>, else null.</summary>
    private static GdsPoint? NearestMarkerTip(
        GdsPoint anchor, IReadOnlyList<GdsPinArrowMarkers.Marker> markers, double toleranceUm)
    {
        GdsPoint? best = null;
        double bestDistanceSquared = toleranceUm * toleranceUm;
        foreach (var marker in markers)
        {
            double dx = marker.Position.X - anchor.X, dy = marker.Position.Y - anchor.Y;
            double distanceSquared = dx * dx + dy * dy;
            if (distanceSquared <= bestDistanceSquared)
            {
                bestDistanceSquared = distanceSquared;
                best = marker.Position;
            }
        }
        return best;
    }

    // Some helpers (edge matching, coordinate conversion, interval merging)
    // live in GdsPinDetector.EdgeHelpers.cs to keep this file under the
    // architecture size limit while remaining in the same partial class.
}
