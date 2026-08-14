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
///
/// The strategies live in sibling partial files: labels/arrow markers in
/// <c>GdsPinDetector.LabelPins.cs</c>, port-layer shapes in
/// <c>GdsPinDetector.PortShapes.cs</c>, the edge heuristic in
/// <c>GdsPinDetector.EdgeScan.cs</c>, terminus faces in
/// <c>GdsPinDetector.TerminusFaces.cs</c>, anchor probing in
/// <c>GdsPinDetector.AnchorProbe.cs</c>, and shared geometry helpers in
/// <c>GdsPinDetector.Geometry.cs</c> / <c>GdsPinDetector.EdgeHelpers.cs</c>.
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

        if (cellBBox.Width <= 0 || cellBBox.Height <= 0)
            return new List<DetectedPin>();

        // Explicit port shapes are detected first: they give a label at the
        // same location its width and exact boundary position, and any shape
        // not named by a label becomes a pin of its own.
        var portShapes = FindPortLayerShapeCandidates(flattened, cellBBox, options);
        var coveredPortShapes = new HashSet<int>();
        var labelAnchors = new List<GdsPoint>();
        var candidates = new List<Candidate>();

        AddLabelAndMarkerPins(candidates, labelAnchors, flattened, cellBBox, portShapes, coveredPortShapes, options);
        AddPortLayerShapePins(candidates, labelAnchors, portShapes, coveredPortShapes, cellBBox, options);
        AddEdgeHeuristicPins(candidates, labelAnchors, flattened, cellBBox, options);

        return OrderAndName(candidates);
    }

    /// <summary>
    /// Deterministic order (edge left→top→right→bottom, then position along
    /// the edge) plus <c>heur_1..N</c> naming for unnamed pins in that order.
    /// </summary>
    private static List<DetectedPin> OrderAndName(List<Candidate> candidates)
    {
        var result = new List<DetectedPin>();
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
}
