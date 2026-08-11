namespace CAP_DataAccess.Import.Gds.LayerCensus;

/// <summary>
/// Content evidence for "this layer is optical": which geometry layers the
/// file's port labels physically REST on. A port label sits at the end of the
/// shape it names — o1/o2 on the waveguide core — so the polygon/path layer
/// under a label carries optical ports with far more certainty than any
/// layer-number convention table (numbers collide across foundries; one
/// foundry's M1 is another's core etch). This probe is deliberately
/// configuration-independent: it never consults the configured
/// waveguide/metal/port layer lists, so its result can arbitrate exactly when
/// those lists (or their defaults) are wrong for the file at hand.
///
/// Labels that are themselves ghosts (nazca bbox anchors, parameter
/// annotations) or carry electrical marker names (pad/gnd/anode/… — labeled
/// bond pads would otherwise mark their metal layer "optical") never
/// contribute. Geometry is evaluated in the reader-normalized µm space; a
/// label attaches when its anchor lies inside a polygon, within
/// <see cref="OutlineTouchToleranceUm"/> of its outline, or within half the
/// width (plus tolerance) of a path's centerline. A label touching several
/// nested shapes attaches to the SMALLEST one only — enclosing
/// cladding/keepout rectangles never inherit the attachment.
/// </summary>
public static class GdsPortAttachmentProbe
{
    /// <summary>
    /// How far a label anchor may sit from a shape's outline and still count
    /// as resting on it. Foundry labels usually land on the shape or directly
    /// beside the edge; 1 µm covers that without bridging to neighbor shapes.
    /// </summary>
    private const double OutlineTouchToleranceUm = 1.0;

    /// <summary>Cap on label names kept per layer (for suggestion reasons).</summary>
    private const int MaxLabelsPerLayer = 4;

    /// <summary>
    /// Scans every cell of the library and returns, per (layer, datatype), the
    /// port labels resting on that layer's shapes (sorted, capped at
    /// <see cref="MaxLabelsPerLayer"/> names; empty layers are omitted).
    /// Deterministic: layers and labels are reported sorted.
    /// </summary>
    public static IReadOnlyDictionary<(int Layer, int Datatype), IReadOnlyList<string>> Scan(
        GdsLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        var attachments = new SortedDictionary<(int, int), List<string>>();
        foreach (var cell in library.Cells.Values)
            ScanCell(cell, attachments);

        return attachments.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<string>)kv.Value.Take(MaxLabelsPerLayer).ToList());
    }

    private static void ScanCell(GdsCell cell, SortedDictionary<(int, int), List<string>> attachments)
    {
        var labels = cell.Elements.OfType<GdsText>()
            .Where(IsPortLabelCandidate)
            .ToList();
        if (labels.Count == 0)
            return;

        var polygons = cell.Elements.OfType<GdsPolygon>()
            .Where(p => p.Points.Count >= 3)
            .Select(p => (Polygon: p, Box: BoundingBox(p.Points)))
            .ToList();
        var paths = cell.Elements.OfType<GdsPath>()
            .Where(p => p.Points.Count >= 2)
            .ToList();
        if (polygons.Count == 0 && paths.Count == 0)
            return;

        foreach (var label in labels)
        {
            // A label may touch several shapes (core + cladding/keepout/bbox
            // rectangles legitimately overlap it): the SMALLEST attaching
            // polygon is the specific shape the label names — enclosing marker
            // rectangles must not inherit the attachment.
            GdsPolygon? bestPolygon = null;
            double bestArea = double.PositiveInfinity;
            foreach (var (polygon, box) in polygons)
            {
                if (!box.ExpandedBy(OutlineTouchToleranceUm).Contains(label.Position))
                    continue;
                if (!PointInPolygon(polygon.Points, label.Position)
                    && DistanceToOutline(polygon.Points, label.Position) > OutlineTouchToleranceUm)
                    continue;
                double area = (box.MaxX - box.MinX) * (box.MaxY - box.MinY);
                if (area < bestArea)
                {
                    bestArea = area;
                    bestPolygon = polygon;
                }
            }
            if (bestPolygon is not null)
            {
                Record(attachments, (bestPolygon.Layer, bestPolygon.DataType), label.Text.Trim());
                continue;
            }
            foreach (var path in paths)
            {
                double reach = path.WidthMicrometers / 2 + OutlineTouchToleranceUm;
                if (DistanceToCenterline(path.Points, label.Position) <= reach)
                    Record(attachments, (path.Layer, path.DataType), label.Text.Trim());
            }
        }
    }

    /// <summary>
    /// Single-line, non-ghost, non-electrical-named text — the shape of a port
    /// label. Electrical markers ("pad", "gnd", …) name bond pads: their labels
    /// resting on a metal rectangle must never mark that layer optical.
    /// </summary>
    private static bool IsPortLabelCandidate(GdsText text)
    {
        if (text.Text.Contains('\n') || GdsGhostLabelFilter.IsGhost(text))
            return false;
        return !GdsPinDetector.ElectricalLabelMarkers.Any(
            marker => text.Text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static void Record(
        SortedDictionary<(int, int), List<string>> attachments, (int, int) pair, string label)
    {
        if (!attachments.TryGetValue(pair, out var labels))
            attachments[pair] = labels = new List<string>();
        if (!labels.Contains(label))
            labels.Add(label);
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
            minX = Math.Min(minX, point.X);
            minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X);
            maxY = Math.Max(maxY, point.Y);
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
