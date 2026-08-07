namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// The label-anchor geometry probe of <see cref="GdsPinDetector"/>: which
/// waveguide/metal outline segment lies nearest a label anchor (the pin
/// direction rule) and which layer classes the anchor touches (the pin kind
/// inference). Candidates come from a <see cref="GdsSpatialGrid"/> built once
/// per detection run — the sequential scan tested every polygon segment for
/// every label, O(labels × segments), and dominated hierarchy import of
/// production-scale files. The grid window (anchor ± tolerance) only prunes
/// geometry that cannot fall within the touch tolerance; the exact distance
/// and point-in-polygon predicates remain the arbiter and candidates are
/// visited in ordinal (element scan) order, so the probe's evidence — and
/// every pin derived from it — is identical to the sequential scan's.
/// </summary>
public static partial class GdsPinDetector
{
    /// <summary>
    /// The geometry evidence around a label anchor, gathered by
    /// <see cref="ProbeAnchorGeometry"/>: the nearest waveguide/metal outline
    /// segment within <see cref="GdsPinDetectionOptions.LabelGeometryTouchToleranceUm"/>
    /// (it drives the pin direction) plus which layer classes the anchor
    /// touches (they drive the kind inference).
    /// </summary>
    /// <param name="Polygon">The polygon owning the nearest in-tolerance segment (needed for its interior probe); null when no segment lies within the tolerance.</param>
    /// <param name="P1">Segment start (GDS space); meaningful only when <see cref="Polygon"/> is set.</param>
    /// <param name="P2">Segment end (GDS space); meaningful only when <see cref="Polygon"/> is set.</param>
    /// <param name="TouchesWaveguide">The anchor lies inside a waveguide polygon or within tolerance of its outline.</param>
    /// <param name="TouchesMetal">The anchor lies inside a metal polygon or within tolerance of its outline.</param>
    private sealed record AnchorGeometry(
        GdsPolygon? Polygon,
        GdsPoint P1,
        GdsPoint P2,
        bool TouchesWaveguide,
        bool TouchesMetal);

    /// <summary>
    /// Label substrings (case-insensitive CONTAINS) marking a pin as ELECTRICAL
    /// when no waveguide/metal polygon is near the anchor — the name-based
    /// fallback of the kind inference. The names PDK black-box cells give their
    /// electrical contacts: anode/cathode (photodetectors, modulators), "elec"
    /// (our own exports and the demofab eopm), bond/supply pads (pad, gnd, vcc,
    /// vdd). Kept deliberately short: every entry must be unambiguous enough
    /// that an OPTICAL port never carries it ("o1", "in", "out", "port0" match
    /// nothing here).
    /// </summary>
    private static readonly string[] ElectricalLabelMarkers =
        ["anode", "cathode", "elec", "pad", "gnd", "vcc", "vdd"];

    /// <summary>One indexed outline segment: endpoints plus the owning polygon's ordinal.</summary>
    private readonly record struct IndexedSegment(GdsPoint P1, GdsPoint P2, int PolygonOrdinal);

    /// <summary>One indexed candidate polygon and its layer class.</summary>
    private readonly record struct IndexedPolygon(GdsPolygon Polygon, bool IsMetal);

    /// <summary>
    /// Per-run spatial index over a flattened cell's waveguide/metal polygons:
    /// outline segments serve the nearest-segment and outline-touch queries,
    /// polygon bounding boxes pre-filter the interior-containment test.
    /// Ordinals follow the sequential scan order (polygons in element order,
    /// segments in vertex order), so sorting grid candidates by ordinal
    /// reproduces the sequential scan's deterministic tie-breaking. The grids
    /// are null when the cell has no candidate geometry.
    /// </summary>
    private sealed class AnchorGeometryIndex
    {
        /// <summary>Candidate polygons in element order.</summary>
        public List<IndexedPolygon> Polygons { get; } = new();

        /// <summary>Non-zero-length outline segments in scan order.</summary>
        public List<IndexedSegment> Segments { get; } = new();

        /// <summary>Grid over segment bounding boxes, indexed by segment ordinal.</summary>
        public GdsSpatialGrid? SegmentGrid { get; set; }

        /// <summary>Grid over polygon bounding boxes, indexed by polygon ordinal.</summary>
        public GdsSpatialGrid? PolygonGrid { get; set; }
    }

    /// <summary>
    /// Indexes every waveguide/metal polygon of the cell. Zero-length segments
    /// are excluded exactly as the probe's distance predicate skips them;
    /// point-free polygons can never contain an anchor and are dropped
    /// entirely (they had no probe-visible effect in the sequential scan
    /// either).
    /// </summary>
    private static AnchorGeometryIndex BuildAnchorGeometryIndex(
        IReadOnlyList<GdsPolygon> polygons, GdsPinDetectionOptions options)
    {
        var index = new AnchorGeometryIndex();
        var polygonBoxes = new List<GdsBoundingBox>();
        foreach (var polygon in polygons)
        {
            // A layer pair configured as both waveguide and metal counts as
            // metal — metal is the stronger (direct electrical) evidence.
            bool isMetal = ContainsLayer(
                options.ElectricalLayers!, polygon.Layer, polygon.DataType); // AUTO resolved in Detect
            bool isWaveguide = !isMetal && ContainsLayer(options.WaveguideLayers, polygon.Layer, polygon.DataType);
            if ((!isMetal && !isWaveguide) || polygon.Points.Count == 0)
                continue;

            int polygonOrdinal = index.Polygons.Count;
            index.Polygons.Add(new IndexedPolygon(polygon, isMetal));
            polygonBoxes.Add(BoundingBoxOf(polygon.Points));
            foreach (var (p1, p2) in Segments(polygon))
            {
                if (!p1.Equals(p2))
                    index.Segments.Add(new IndexedSegment(p1, p2, polygonOrdinal));
            }
        }
        PopulateGrids(index, polygonBoxes, options.LabelGeometryTouchToleranceUm);
        return index;
    }

    /// <summary>
    /// Creates and fills both grids, sized to the overall geometry span and the
    /// label touch tolerance. No-op when the cell has no candidate polygons.
    /// </summary>
    private static void PopulateGrids(
        AnchorGeometryIndex index, List<GdsBoundingBox> polygonBoxes, double toleranceUm)
    {
        if (index.Polygons.Count == 0)
            return;

        var overall = polygonBoxes[0];
        foreach (var box in polygonBoxes)
            overall = overall.Union(box);
        double span = Math.Max(overall.Width, overall.Height);

        index.PolygonGrid = GdsSpatialGrid.Create(span, toleranceUm, index.Polygons.Count);
        for (int i = 0; i < polygonBoxes.Count; i++)
        {
            var box = polygonBoxes[i];
            index.PolygonGrid.InsertBox(i, box.MinX, box.MinY, box.MaxX, box.MaxY);
        }

        if (index.Segments.Count == 0)
            return;
        index.SegmentGrid = GdsSpatialGrid.Create(span, toleranceUm, index.Segments.Count);
        for (int i = 0; i < index.Segments.Count; i++)
        {
            var segment = index.Segments[i];
            index.SegmentGrid.InsertBox(i,
                Math.Min(segment.P1.X, segment.P2.X), Math.Min(segment.P1.Y, segment.P2.Y),
                Math.Max(segment.P1.X, segment.P2.X), Math.Max(segment.P1.Y, segment.P2.Y));
        }
    }

    /// <summary>
    /// Gathers the waveguide/metal geometry evidence around a label anchor: the
    /// candidate polygon (waveguide + electrical layers) whose OUTLINE segment
    /// comes closest to the anchor within the touch tolerance — that segment
    /// drives the pin direction — plus which layer classes the anchor TOUCHES
    /// (inside the polygon or within tolerance of its outline — the same touch
    /// union <c>GdsRouteConnectivityMatcher</c> uses) — that drives the kind
    /// inference. Null when the anchor has neither an in-tolerance segment nor
    /// an interior containment. Deterministic: grid candidates are sorted back
    /// into element scan order and a strictly smaller distance wins, so ties
    /// keep the earliest polygon/segment exactly like the sequential scan.
    /// </summary>
    private static AnchorGeometry? ProbeAnchorGeometry(
        GdsPoint anchor, AnchorGeometryIndex index, GdsPinDetectionOptions options)
    {
        double tolerance = options.LabelGeometryTouchToleranceUm;
        double toleranceSquared = tolerance * tolerance;
        double bestDistanceSquared = double.PositiveInfinity;
        int bestSegmentOrdinal = -1;
        bool touchesWaveguide = false, touchesMetal = false;

        if (index.SegmentGrid is not null)
        {
            var segmentOrdinals = index.SegmentGrid.QueryBox(
                anchor.X - tolerance, anchor.Y - tolerance,
                anchor.X + tolerance, anchor.Y + tolerance);
            segmentOrdinals.Sort();
            foreach (int ordinal in segmentOrdinals)
            {
                var segment = index.Segments[ordinal];
                double distanceSquared = DistanceToSegmentSquared(anchor, segment.P1, segment.P2);
                if (distanceSquared > toleranceSquared)
                    continue;
                MarkTouch(index.Polygons[segment.PolygonOrdinal].IsMetal, ref touchesWaveguide, ref touchesMetal);
                if (distanceSquared < bestDistanceSquared)
                {
                    bestDistanceSquared = distanceSquared;
                    bestSegmentOrdinal = ordinal;
                }
            }
        }
        ProbeInteriorContainment(anchor, index, ref touchesWaveguide, ref touchesMetal);

        if (bestSegmentOrdinal < 0)
        {
            return touchesWaveguide || touchesMetal
                ? new AnchorGeometry(null, default, default, touchesWaveguide, touchesMetal)
                : null;
        }
        var best = index.Segments[bestSegmentOrdinal];
        return new AnchorGeometry(
            index.Polygons[best.PolygonOrdinal].Polygon, best.P1, best.P2, touchesWaveguide, touchesMetal);
    }

    /// <summary>
    /// Sets the touch flags for polygons CONTAINING the anchor: the polygon
    /// grid pre-filters to bounding boxes overlapping the anchor, the exact
    /// even-odd test decides. The flags are an OR-union, so candidate order is
    /// irrelevant, and a layer class already proven touching by the outline
    /// pass needs no further containment test.
    /// </summary>
    private static void ProbeInteriorContainment(
        GdsPoint anchor, AnchorGeometryIndex index, ref bool touchesWaveguide, ref bool touchesMetal)
    {
        if (index.PolygonGrid is null || (touchesWaveguide && touchesMetal))
            return;
        foreach (int ordinal in index.PolygonGrid.QueryBox(anchor.X, anchor.Y, anchor.X, anchor.Y))
        {
            var candidate = index.Polygons[ordinal];
            if (candidate.IsMetal ? touchesMetal : touchesWaveguide)
                continue;
            if (PointInPolygon(candidate.Polygon.Points, anchor))
                MarkTouch(candidate.IsMetal, ref touchesWaveguide, ref touchesMetal);
        }
    }

    /// <summary>Records a touch for the layer class of one piece of evidence.</summary>
    private static void MarkTouch(bool isMetal, ref bool touchesWaveguide, ref bool touchesMetal)
    {
        if (isMetal)
            touchesMetal = true;
        else
            touchesWaveguide = true;
    }

    /// <summary>
    /// Infers a label pin's signal domain. Layer evidence is primary: touching
    /// a metal polygon proves ELECTRICAL (metal only carries electrical
    /// signals). Touching only waveguide polygons stays kind-UNKNOWN (null, the
    /// optical default downstream) rather than proven-optical: a later
    /// metal-route match (<c>GdsRouteConnectivityMatcher</c>) is stronger
    /// physical evidence and must still be able to infer the pin electrical.
    /// With no polygon near, the label text decides
    /// (<see cref="ElectricalLabelMarkers"/>); anything else stays unknown.
    /// </summary>
    private static bool? InferLabelPinKind(string label, AnchorGeometry? geometry)
    {
        if (geometry is not null)
        {
            if (geometry.TouchesMetal)
                return true;
            if (geometry.TouchesWaveguide)
                return null; // waveguide evidence beats the name heuristic
        }

        foreach (string marker in ElectricalLabelMarkers)
        {
            if (label.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return null;
    }
}
