namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// Finds nazca-style arrow pin markers in a flattened cell: small chevron
/// polygons on dedicated marker layers. A PAIR of arrows whose tips (nearly)
/// touch marks a pin at the meeting point. Single chevrons are deliberately
/// IGNORED: in nazca conventions they are the orientation markers that merely
/// indicate "outside" for a cell edge, and treating them as pins invents
/// false ports (demofab files carry four of them per device). The pair tips
/// carry exact pin POSITIONS; the pin's outward direction and material are
/// probed from the route geometry at that position by the caller
/// (<see cref="GdsPinDetector"/>), so no layer numbers or foundry conventions
/// are baked in here.
///
/// Shape recognition is deliberately conservative so real geometry never
/// matches: few vertices, sub-3 µm span, at least one sharp convex corner
/// (chevrons/triangles have one; rectangles and vias do not), the layer must
/// carry ONLY such shapes (a waveguide layer's small tapers are device
/// geometry, not markers), and the meeting point must sit near the cell's
/// bounding-box edge — where pins of a device cell live.
/// </summary>
internal static class GdsPinArrowMarkers
{
    /// <summary>One pin position derived from an arrow-marker pair (GDS space, µm).</summary>
    internal sealed record Marker(GdsPoint Position, int Layer, int Datatype);

    /// <summary>Marker chevrons are sub-µm to a few µm (electrical pin markers reach 5 µm); larger shapes are real geometry.</summary>
    private const double MaxMarkerSpanUm = 6.0;

    /// <summary>Markers have few vertices (the nazca chevron has 7).</summary>
    private const int MaxMarkerVertices = 12;

    /// <summary>A convex corner sharper than this (interior degrees) makes a polygon "pointed".</summary>
    private const double SharpVertexMaxDegrees = 90.0;

    /// <summary>How far apart two arrow tips may be to count as meeting at one pin.</summary>
    private const double PairingToleranceUm = 0.5;

    /// <summary>Pin positions of a device cell sit on its bounding-box outline.</summary>
    private const double EdgeProximityToleranceUm = 2.0;

    /// <summary>
    /// Finds pin positions from arrow markers: paired chevrons first (tips
    /// meeting), then unpaired singles near the cell edge. Deterministic:
    /// candidates are scanned in element order, each arrow pairs at most once
    /// (with its earliest partner), results are ordered by first appearance.
    /// </summary>
    internal static IReadOnlyList<Marker> Find(FlattenedGdsCell cell, GdsBoundingBox cellBBox)
    {
        // A marker layer carries ONLY marker-shaped polygons. A layer that
        // also carries real geometry (cores, routes, tapers) is never a
        // marker layer — its small pointed shapes are device geometry
        // (adiabatic tapers are small, pointed and near the edge, and must
        // not become pins).
        var candidates = new List<(List<GdsPoint> Points, int Layer, int Datatype)>();
        foreach (var layerGroup in cell.Polygons.GroupBy(p => (p.Layer, p.DataType)))
        {
            var normalized = layerGroup
                .Select(p => (Points: Normalize(p.Points), p.Layer, p.DataType))
                .ToList();
            if (normalized.Any(n => !IsMarkerShaped(n.Points)))
                continue;
            candidates.AddRange(normalized);
        }

        var markers = new List<Marker>();
        var paired = new bool[candidates.Count];
        for (int i = 0; i < candidates.Count; i++)
        {
            if (paired[i])
                continue;
            for (int j = i + 1; j < candidates.Count; j++)
            {
                if (paired[j])
                    continue;
                // Pairs may span DIFFERENT marker layers — some conventions
                // place one chevron per pin per layer, meeting tip-to-tip.
                if (!ClosestVertices(candidates[i].Points, candidates[j].Points,
                        out var onA, out var onB, out double distance)
                    || distance > PairingToleranceUm)
                    continue;
                var position = new GdsPoint((onA.X + onB.X) / 2, (onA.Y + onB.Y) / 2);
                if (NearBBoxOutline(position, cellBBox))
                {
                    markers.Add(new Marker(position, candidates[i].Layer, candidates[i].Datatype));
                    paired[i] = paired[j] = true;
                }
                break; // one partner per arrow — a second near-tip arrow is a new pin's arrow
            }
            // Unpaired chevrons are orientation markers, never pins.
        }
        return markers;
    }

    /// <summary>Drops a duplicated closing point so vertex indexing walks each corner once.</summary>
    private static List<GdsPoint> Normalize(IReadOnlyList<GdsPoint> points)
    {
        var list = points.ToList();
        if (list.Count > 1 && list[0].Equals(list[^1]))
            list.RemoveAt(list.Count - 1);
        return list;
    }

    /// <summary>The marker shape rule: few vertices, small span, one sharp convex corner.</summary>
    /// <summary>True when every polygon on the layer is marker-shaped — a dedicated marker layer.</summary>
    internal static bool IsMarkerLayer(IReadOnlyList<GdsPolygon> layerPolygons) =>
        layerPolygons.Count > 0 && layerPolygons.All(p => IsMarkerShaped(Normalize(p.Points)));

    private static bool IsMarkerShaped(List<GdsPoint> points)
    {
        if (points.Count is < 3 or > MaxMarkerVertices)
            return false;
        if (!SpanWithin(points, MaxMarkerSpanUm))
            return false;
        return HasSharpConvexVertex(points);
    }

    private static bool SpanWithin(List<GdsPoint> points, double maxSpan)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in points)
        {
            minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
        }
        double span = Math.Max(maxX - minX, maxY - minY);
        return span > 0 && span <= maxSpan;
    }

    /// <summary>True when any convex vertex has an interior angle sharper than 90° (chevron tip).</summary>
    private static bool HasSharpConvexVertex(List<GdsPoint> points)
    {
        double signedArea = SignedArea2(points);
        if (signedArea == 0)
            return false;
        bool ccw = signedArea > 0;
        for (int i = 0; i < points.Count; i++)
        {
            var prev = points[(i + points.Count - 1) % points.Count];
            var curr = points[i];
            var next = points[(i + 1) % points.Count];
            double inX = curr.X - prev.X, inY = curr.Y - prev.Y;
            double outX = next.X - curr.X, outY = next.Y - curr.Y;
            double turnDeg = Math.Atan2(inX * outY - inY * outX, inX * outX + inY * outY) * 180 / Math.PI;
            double interior = ccw ? 180 - turnDeg : 180 + turnDeg;
            if (interior < SharpVertexMaxDegrees)
                return true;
        }
        return false;
    }

    private static double SignedArea2(List<GdsPoint> points)
    {
        double area2 = 0;
        for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
            area2 += points[j].X * points[i].Y - points[i].X * points[j].Y;
        return area2;
    }

    /// <summary>The closest vertex pair between two marker polygons.</summary>
    private static bool ClosestVertices(
        List<GdsPoint> a, List<GdsPoint> b, out GdsPoint onA, out GdsPoint onB, out double distance)
    {
        onA = default;
        onB = default;
        distance = double.PositiveInfinity;
        foreach (var pa in a)
        {
            foreach (var pb in b)
            {
                double d = Math.Sqrt((pa.X - pb.X) * (pa.X - pb.X) + (pa.Y - pb.Y) * (pa.Y - pb.Y));
                if (d < distance)
                {
                    distance = d;
                    onA = pa;
                    onB = pb;
                }
            }
        }
        return !double.IsPositiveInfinity(distance);
    }

    private static bool NearBBoxOutline(GdsPoint point, GdsBoundingBox bbox) =>
        DistanceToBBoxOutline(point, bbox) <= EdgeProximityToleranceUm;

    private static double DistanceToBBoxOutline(GdsPoint point, GdsBoundingBox bbox)
    {
        double dx = Math.Max(bbox.MinX - point.X, 0) + Math.Max(point.X - bbox.MaxX, 0);
        double dy = Math.Max(bbox.MinY - point.Y, 0) + Math.Max(point.Y - bbox.MaxY, 0);
        if (dx > 0 || dy > 0)
            return Math.Sqrt(dx * dx + dy * dy); // outside the box entirely
        double toEdge = Math.Min(
            Math.Min(point.X - bbox.MinX, bbox.MaxX - point.X),
            Math.Min(point.Y - bbox.MinY, bbox.MaxY - point.Y));
        return toEdge;
    }
}
