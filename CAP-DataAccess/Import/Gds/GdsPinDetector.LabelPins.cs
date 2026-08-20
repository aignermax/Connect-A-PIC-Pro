namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// Label and arrow-marker pin detection for <see cref="GdsPinDetector"/>:
/// the strategies that turn TEXT elements on port layers and nazca-style
/// arrow markers into named/unnamed pins, including the label↔port-shape
/// coupling (a label adopts a boundary port shape's exact midpoint and
/// width). Split out to keep the orchestrating file within the
/// architecture size limit.
/// </summary>
public static partial class GdsPinDetector
{
    /// <summary>How far a label anchor may sit from an arrow-marker tip to adopt its exact position.</summary>
    private const double MarkerSnapToleranceUm = 2.0;

    /// <summary>
    /// Adds label pins (strategy 1) and label-free arrow-marker pins
    /// (strategy 1b) to <paramref name="candidates"/>. Every accepted anchor
    /// is recorded in <paramref name="labelAnchors"/> so later strategies do
    /// not duplicate it; port shapes consumed by a label are recorded in
    /// <paramref name="coveredPortShapes"/>.
    /// </summary>
    private static void AddLabelAndMarkerPins(
        List<Candidate> candidates,
        List<GdsPoint> labelAnchors,
        FlattenedGdsCell flattened,
        GdsBoundingBox cellBBox,
        IReadOnlyList<PortShapeCandidate> portShapes,
        HashSet<int> coveredPortShapes,
        GdsPinDetectionOptions options)
    {
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
                Layer = text.Layer,
                Source = DetectedPinSource.Label,
                IsElectrical = InferLabelPinKind(text.Text, geometry),
            }));
        }

        AddUnlabeledMarkerPins(candidates, labelAnchors, flattened, cellBBox, markers, geometryIndex, options);
    }

    /// <summary>
    /// Markers without a nearby label still yield a pin — but only when route
    /// geometry lies at the tip: floating orientation chevrons (they merely
    /// indicate "outside" for an edge) never become pins.
    /// </summary>
    private static void AddUnlabeledMarkerPins(
        List<Candidate> candidates,
        List<GdsPoint> labelAnchors,
        FlattenedGdsCell flattened,
        GdsBoundingBox cellBBox,
        IReadOnlyList<GdsPinArrowMarkers.Marker> markers,
        AnchorGeometryIndex? geometryIndex,
        GdsPinDetectionOptions options)
    {
        if (markers.Count == 0)
            return;
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
                Layer = geometry.Polygon?.Layer,
                Source = DetectedPinSource.ArrowMarker,
                IsElectrical = InferLabelPinKind(string.Empty, geometry),
            }));
        }
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
}
