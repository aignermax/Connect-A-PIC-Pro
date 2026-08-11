namespace CAP_DataAccess.Import.Gds.LayerCensus;

/// <summary>
/// Content evidence for "this layer is optical": which geometry layers the
/// file's port labels physically REST on. A port label sits at the end of the
/// shape it names — o1/o2 on the waveguide core — so the shape under a label
/// carries optical ports with far more certainty than any layer-number
/// convention table (numbers collide across foundries; one foundry's M1 is
/// another's core etch). This probe is deliberately configuration-independent:
/// it never consults the configured waveguide/metal/port layer lists, so its
/// result can arbitrate exactly when those lists (or their defaults) are wrong
/// for the file at hand.
///
/// Rules, learned from real foundry files:
/// - Empty, multi-line, ghost (bbox anchors / parameter annotations) and
///   electrically-named (pad/gnd/anode/…, or contact-style e1/n1/p1) labels
///   never vote.
/// - A label attaches to the most SPECIFIC shape it touches (smallest bbox
///   area): foundries stamp a dedicated pin square on the waveguide layer
///   exactly where the pin marker tips sit. Cell-covering background
///   rectangles are skipped while anything else touches.
/// - Equal-size overlaps (e.g. an annotation backing square stacked on the
///   pin square) are broken by file-wide content: the layer that carries
///   device geometry everywhere beats the annotation layer.
/// - One stray label proves little: the result carries the vote COUNT per
///   layer, and only a quorum (<see cref="MinVotesForProof"/>) is treated as
///   proof by the suggestion engine.
/// </summary>
public static class GdsPortAttachmentProbe
{
    /// <summary>
    /// How far a label anchor may sit from a shape's outline and still count
    /// as resting on it. Foundry labels usually land on the shape or directly
    /// beside the edge; 1 µm covers that without bridging to neighbor shapes.
    /// </summary>
    private const double OutlineTouchToleranceUm = 1.0;

    /// <summary>Shapes covering most of the cell are frames/backgrounds, never pin shapes.</summary>
    private const double BackgroundAreaFraction = 0.8;

    /// <summary>Cap on label names kept per layer (for suggestion reasons).</summary>
    private const int MaxLabelsPerLayer = 4;

    /// <summary>Votes a layer needs before its optical claim counts as proven.</summary>
    public const int MinVotesForProof = 2;

    /// <summary>One layer's attachment evidence: how many port labels rest on it, plus a name sample.</summary>
    public sealed record AttachmentEvidence(int Count, IReadOnlyList<string> SampleLabels);

    /// <summary>
    /// Scans every cell of the library and returns, per (layer, datatype), the
    /// attachment evidence of port labels resting on that layer's shapes.
    /// Deterministic: layers sorted, samples in scan order.
    /// </summary>
    public static IReadOnlyDictionary<(int Layer, int Datatype), AttachmentEvidence> Scan(
        GdsLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        // File-wide polygon content per layer: device-geometry layers are used
        // in every cell, annotation/backing layers carry a handful of shapes.
        var content = new Dictionary<(int, int), int>();
        foreach (var cell in library.Cells.Values)
        {
            foreach (var polygon in cell.Elements.OfType<GdsPolygon>())
            {
                var key = (polygon.Layer, polygon.DataType);
                content[key] = content.TryGetValue(key, out var n) ? n + 1 : 1;
            }
        }

        var votes = new Dictionary<(int, int), int>();
        var samples = new SortedDictionary<(int, int), List<string>>();
        foreach (var cell in library.Cells.Values)
            ScanCell(cell, content, votes, samples);

        return votes.ToDictionary(
            kv => kv.Key,
            kv => new AttachmentEvidence(
                kv.Value,
                (IReadOnlyList<string>)samples[kv.Key].Take(MaxLabelsPerLayer).ToList()));
    }

    private static void ScanCell(
        GdsCell cell,
        IReadOnlyDictionary<(int, int), int> content,
        Dictionary<(int, int), int> votes,
        SortedDictionary<(int, int), List<string>> samples)
    {
        var labels = cell.Elements.OfType<GdsText>()
            .Where(IsPortLabelCandidate)
            .ToList();
        if (labels.Count == 0)
            return;

        var cellBox = CellBoundingBox(cell);
        var polygons = cell.Elements.OfType<GdsPolygon>()
            .Where(p => p.Points.Count >= 3)
            .Select(p => (Polygon: p, Box: BoundingBox(p.Points)))
            .ToList();
        var paths = cell.Elements.OfType<GdsPath>()
            .Where(p => p.Points.Count >= 2)
            .ToList();
        if (polygons.Count == 0 && paths.Count == 0)
            return;

        // Marker chevrons sit exactly at the pin and are the smallest shapes
        // around — without excluding their layers they would "prove" the pin
        // marker layer itself optical.
        var markerLayers = polygons
            .GroupBy(p => (p.Polygon.Layer, p.Polygon.DataType))
            .Where(g => GdsPinArrowMarkers.IsMarkerLayer(g.Select(x => x.Polygon).ToList()))
            .Select(g => g.Key)
            .ToHashSet();
        var attachable = markerLayers.Count == 0
            ? polygons
            : polygons.Where(p => !markerLayers.Contains((p.Polygon.Layer, p.Polygon.DataType))).ToList();

        foreach (var label in labels)
        {
            var bestPolygon = PickMostSpecific(attachable, cellBox, content, label.Position, ignoreBackground: true)
                ?? PickMostSpecific(attachable, cellBox, content, label.Position, ignoreBackground: false);
            if (bestPolygon is not null)
            {
                Record(votes, samples, (bestPolygon.Layer, bestPolygon.DataType), label.Text.Trim());
                continue;
            }
            foreach (var path in paths)
            {
                double reach = path.WidthMicrometers / 2 + OutlineTouchToleranceUm;
                if (DistanceToCenterline(path.Points, label.Position) <= reach)
                    Record(votes, samples, (path.Layer, path.DataType), label.Text.Trim());
            }
        }
    }

    /// <summary>
    /// The most specific touching polygon: smallest bbox area wins (foundry
    /// pin squares sit exactly at the pin), ties break toward the layer with
    /// more file-wide polygon content (device geometry beats annotation
    /// backings), then toward the lower layer number for determinism. With
    /// <paramref name="ignoreBackground"/>, cell-covering shapes are skipped.
    /// </summary>
    private static GdsPolygon? PickMostSpecific(
        List<(GdsPolygon Polygon, Box Box)> polygons,
        Box cellBox,
        IReadOnlyDictionary<(int, int), int> content,
        GdsPoint position,
        bool ignoreBackground)
    {
        GdsPolygon? bestPolygon = null;
        double bestArea = double.PositiveInfinity;
        int bestContent = -1;
        foreach (var (polygon, box) in polygons)
        {
            if (ignoreBackground && IsBackground(box, cellBox))
                continue;
            if (!box.ExpandedBy(OutlineTouchToleranceUm).Contains(position))
                continue;
            if (!PointInPolygon(polygon.Points, position)
                && DistanceToOutline(polygon.Points, position) > OutlineTouchToleranceUm)
                continue;
            double area = Area(box);
            int layerContent = content.GetValueOrDefault((polygon.Layer, polygon.DataType));
            bool tied = Math.Abs(area - bestArea) <= 1e-9;
            if (area < bestArea - 1e-9
                || (tied && layerContent > bestContent)
                || (tied && layerContent == bestContent && bestPolygon is not null
                    && (polygon.Layer, polygon.DataType).CompareTo((bestPolygon.Layer, bestPolygon.DataType)) < 0))
            {
                bestPolygon = polygon;
                bestArea = area;
                bestContent = layerContent;
            }
        }
        return bestPolygon;
    }

    private static bool IsBackground(Box box, Box cellBox) =>
        Area(cellBox) > 0 && Area(box) >= BackgroundAreaFraction * Area(cellBox);

    private static double Area(Box box) => (box.MaxX - box.MinX) * (box.MaxY - box.MinY);

    private static Box CellBoundingBox(GdsCell cell)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var polygon in cell.Elements.OfType<GdsPolygon>())
        {
            foreach (var p in polygon.Points)
            {
                minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
                maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
            }
        }
        foreach (var text in cell.Elements.OfType<GdsText>())
        {
            minX = Math.Min(minX, text.Position.X); minY = Math.Min(minY, text.Position.Y);
            maxX = Math.Max(maxX, text.Position.X); maxY = Math.Max(maxY, text.Position.Y);
        }
        return minX > maxX ? new Box(0, 0, 0, 0) : new Box(minX, minY, maxX, maxY);
    }

    /// <summary>
    /// Non-empty, single-line, non-ghost, non-electrical-named text — the
    /// shape of a port label. Electrical names ("pad", "gnd", …, contact-style
    /// e1/n1/p1) name pads/contacts: their labels resting on a metal rectangle
    /// must never mark that layer optical.
    /// </summary>
    private static bool IsPortLabelCandidate(GdsText text)
    {
        if (string.IsNullOrWhiteSpace(text.Text)
            || text.Text.Contains('\n')
            || GdsGhostLabelFilter.IsGhost(text))
            return false;
        return !GdsPinDetector.IsElectricalLabelName(text.Text);
    }

    private static void Record(
        Dictionary<(int, int), int> votes,
        SortedDictionary<(int, int), List<string>> samples,
        (int, int) pair, string label)
    {
        votes[pair] = votes.TryGetValue(pair, out var count) ? count + 1 : 1;
        if (!samples.TryGetValue(pair, out var list))
            samples[pair] = list = new List<string>();
        if (!list.Contains(label))
            list.Add(label);
    }

    private readonly record struct Box(double MinX, double MinY, double MaxX, double MaxY)
    {
        public Box ExpandedBy(double margin) =>
            new(MinX - margin, MinY - margin, MaxX + margin, MaxY + margin);

        public bool Contains(GdsPoint point) =>
            point.X >= MinX && point.X <= MaxX && point.Y >= MinY && point.Y <= MaxY;
    }

    private static Box BoundingBox(IReadOnlyList<GdsPoint> points)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var point in points)
        {
            minX = Math.Min(minX, point.X); minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X); maxY = Math.Max(maxY, point.Y);
        }
        return new Box(minX, minY, maxX, maxY);
    }

    /// <summary>Even-odd point-in-polygon test.</summary>
    private static bool PointInPolygon(IReadOnlyList<GdsPoint> points, GdsPoint point)
    {
        bool inside = false;
        for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
        {
            var a = points[i];
            var b = points[j];
            if (a.Y > point.Y != b.Y > point.Y
                && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    private static double DistanceToOutline(IReadOnlyList<GdsPoint> points, GdsPoint point)
    {
        double best = double.PositiveInfinity;
        for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
            best = Math.Min(best, DistanceToSegment(point, points[j], points[i]));
        return best;
    }

    private static double DistanceToCenterline(IReadOnlyList<GdsPoint> points, GdsPoint point)
    {
        double best = double.PositiveInfinity;
        for (int i = 0; i < points.Count - 1; i++)
            best = Math.Min(best, DistanceToSegment(point, points[i], points[i + 1]));
        return best;
    }

    private static double DistanceToSegment(GdsPoint point, GdsPoint a, GdsPoint b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double lengthSquared = dx * dx + dy * dy;
        if (lengthSquared == 0)
            return Math.Sqrt((point.X - a.X) * (point.X - a.X) + (point.Y - a.Y) * (point.Y - a.Y));
        double t = Math.Clamp(((point.X - a.X) * dx + (point.Y - a.Y) * dy) / lengthSquared, 0, 1);
        double projX = a.X + t * dx - point.X, projY = a.Y + t * dy - point.Y;
        return Math.Sqrt(projX * projX + projY * projY);
    }
}
