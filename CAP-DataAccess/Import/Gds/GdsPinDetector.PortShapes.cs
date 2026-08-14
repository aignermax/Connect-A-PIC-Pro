namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// Port-layer shape detection for <see cref="GdsPinDetector"/>: polygons drawn
/// on configured port layers that cross the cell boundary are explicit port
/// markers. They yield a pin at the boundary intersection, with the segment
/// length as width and the cell edge's outward normal as direction. A nearby
/// label on the same port layer names the pin; otherwise it keeps a heuristic
/// name. Split out to keep the main detector file from growing.
/// </summary>
public static partial class GdsPinDetector
{
    /// <summary>
    /// A candidate port derived from a port-layer polygon edge lying on the
    /// cell bounding-box outline.
    /// </summary>
    private readonly record struct PortShapeCandidate(
        CellEdge Edge,
        GdsPoint Midpoint,
        double WidthUm,
        (int Layer, int Datatype) LayerPair);

    /// <summary>
    /// Finds all port-layer polygon edges that lie on the cell boundary and
    /// converts them into candidate pins. Adjacent/overlapping touches on the
    /// same edge are merged so a single physical port represented by several
    /// abutting polygons does not split into multiple pins.
    /// </summary>
    private static IReadOnlyList<PortShapeCandidate> FindPortLayerShapeCandidates(
        FlattenedGdsCell flattened,
        GdsBoundingBox cellBBox,
        GdsPinDetectionOptions options)
    {
        var touches = new SortedList<CellEdge, List<(double Start, double End, (int Layer, int Datatype) Pair)>>();

        foreach (var polygon in flattened.Polygons)
        {
            if (!ContainsLayer(options.PortLayers, polygon.Layer, polygon.DataType))
                continue;

            foreach (var (p1, p2) in Segments(polygon))
            {
                CellEdge? edge = TouchingEdge(p1, p2, cellBBox, options.EdgeTouchToleranceUm);
                if (edge is null)
                    continue;

                (double start, double end) = edge is CellEdge.Left or CellEdge.Right
                    ? (Math.Min(p1.Y, p2.Y), Math.Max(p1.Y, p2.Y))
                    : (Math.Min(p1.X, p2.X), Math.Max(p1.X, p2.X));

                if (!touches.TryGetValue(edge.Value, out var list))
                    touches.Add(edge.Value, list = new List<(double, double, (int, int))>());
                list.Add((start, end, (polygon.Layer, polygon.DataType)));
            }
        }

        var candidates = new List<PortShapeCandidate>();
        foreach (var (edge, intervals) in touches)
        {
            var merged = MergePortShapeIntervals(intervals, options.EdgeTouchToleranceUm);
            foreach (var (start, end, pair) in merged)
            {
                double width = end - start;
                if (width < options.MinPinWidthUm || width > options.MaxPinWidthUm)
                    continue;

                GdsPoint midpoint = MidpointOnEdge(edge, (start + end) / 2.0, cellBBox);
                candidates.Add(new PortShapeCandidate(edge, midpoint, width, pair));
            }
        }
        return candidates;
    }

    /// <summary>
    /// Merges port-shape intervals on one edge. The layer pair of the first
    /// interval in a merged run is kept — a single physical port should not
    /// straddle layers with different electrical roles.
    /// </summary>
    private static List<(double Start, double End, (int Layer, int Datatype) Pair)> MergePortShapeIntervals(
        List<(double Start, double End, (int Layer, int Datatype) Pair)> intervals,
        double tolerance)
    {
        intervals.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(double Start, double End, (int Layer, int Datatype) Pair)>();
        foreach (var (start, end, pair) in intervals)
        {
            if (merged.Count > 0 && start <= merged[^1].End + tolerance)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, end), merged[^1].Pair);
            }
            else
            {
                merged.Add((start, end, pair));
            }
        }
        return merged;
    }

    /// <summary>
    /// The nearest port-shape candidate within <paramref name="toleranceUm"/>
    /// of <paramref name="point"/>, or null. Returns the candidate and its
    /// index so callers can track which shapes have been consumed by labels.
    /// </summary>
    private static (int Index, PortShapeCandidate Candidate)? NearestPortShape(
        GdsPoint point,
        IReadOnlyList<PortShapeCandidate> candidates,
        double toleranceUm)
    {
        (int Index, PortShapeCandidate Candidate)? best = null;
        double bestDistanceSquared = toleranceUm * toleranceUm;
        for (int i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            double dx = candidate.Midpoint.X - point.X;
            double dy = candidate.Midpoint.Y - point.Y;
            double distanceSquared = dx * dx + dy * dy;
            if (distanceSquared <= bestDistanceSquared)
            {
                bestDistanceSquared = distanceSquared;
                best = (i, candidate);
            }
        }
        return best;
    }

    /// <summary>
    /// True when the polygon layer of the port-shape candidate is configured
    /// as an electrical (metal) layer — the shape itself is direct evidence
    /// that the pin carries an electrical signal.
    /// </summary>
    private static bool IsPortShapeElectrical(PortShapeCandidate candidate, GdsPinDetectionOptions options) =>
        ContainsLayer(options.ElectricalLayers, candidate.LayerPair.Layer, candidate.LayerPair.Datatype);

    /// <summary>
    /// Adds pins for port-layer polygon shapes that are not already named by a
    /// label. Label pins that cover a shape get the shape's width and position
    /// instead; this method supplies the unnamed remainder. Each added shape's
    /// midpoint is recorded as a label anchor so the edge heuristic does not
    /// duplicate the intentional port marker.
    /// </summary>
    private static void AddPortLayerShapePins(
        List<Candidate> candidates,
        List<GdsPoint> labelAnchors,
        IReadOnlyList<PortShapeCandidate> portShapes,
        HashSet<int> coveredPortShapeIndexes,
        GdsBoundingBox cellBBox,
        GdsPinDetectionOptions options)
    {
        for (int i = 0; i < portShapes.Count; i++)
        {
            var shape = portShapes[i];
            if (coveredPortShapeIndexes.Contains(i))
                continue;
            if (IsCoveredByLabel(shape.Midpoint, labelAnchors, options.EdgeTouchToleranceUm))
                continue;

            candidates.Add(new Candidate(shape.Edge, new DetectedPin
            {
                Name = string.Empty,
                XUm = ToAppX(shape.Midpoint.X, cellBBox),
                YUm = ToAppY(shape.Midpoint.Y, cellBBox),
                AngleDegrees = OutwardAngleDegrees(shape.Edge),
                WidthUm = shape.WidthUm,
                Source = DetectedPinSource.PortShape,
                IsElectrical = IsPortShapeElectrical(shape, options) ? true : null,
            }));
            labelAnchors.Add(shape.Midpoint);
        }
    }
}
