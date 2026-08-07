namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// Simplifies draft outline polygons with the Ramer-Douglas-Peucker algorithm
/// and enforces the per-cell point cap: when the simplified polygons still
/// exceed the cap, the tolerance is raised adaptively (×4 per round, up to 8
/// rounds); as a last resort the smallest-area polygons are dropped.
/// </summary>
internal static class GdsOutlineSimplifier
{
    /// <summary>
    /// Simplifies app-space outline polygons. <paramref name="droppedPolygonCount"/>
    /// reports how many polygons were dropped to satisfy the cap (0 = none).
    /// Polygons that collapse below a triangle under simplification are removed.
    /// </summary>
    public static IReadOnlyList<GdsOutlinePolygon> Simplify(
        IReadOnlyList<GdsOutlinePolygon> polygons,
        double toleranceUm,
        int maxTotalPoints,
        out int droppedPolygonCount)
    {
        droppedPolygonCount = 0;
        IReadOnlyList<GdsOutlinePolygon> current = SimplifyAll(polygons, Math.Max(0, toleranceUm));
        double grownTolerance = Math.Max(toleranceUm, 1e-6);
        for (int round = 0; round < 8 && TotalPoints(current) > maxTotalPoints; round++)
        {
            grownTolerance *= 4;
            current = SimplifyAll(polygons, grownTolerance);
        }

        if (TotalPoints(current) > maxTotalPoints)
        {
            current = DropSmallestPolygons(current, maxTotalPoints, out droppedPolygonCount);
        }
        return current;
    }

    private static List<GdsOutlinePolygon> SimplifyAll(
        IReadOnlyList<GdsOutlinePolygon> polygons, double toleranceUm)
    {
        var result = new List<GdsOutlinePolygon>(polygons.Count);
        foreach (var polygon in polygons)
        {
            var points = RamerDouglasPeucker(polygon.Points, toleranceUm);
            // A closed ring needs at least a triangle: 3 distinct points + closing point.
            if (points.Count < 4)
                continue;
            result.Add(polygon with { Points = points });
        }
        return result;
    }

    private static int TotalPoints(IReadOnlyList<GdsOutlinePolygon> polygons)
    {
        int total = 0;
        foreach (var polygon in polygons)
            total += polygon.Points.Count;
        return total;
    }

    /// <summary>
    /// Keeps the largest polygons by absolute area until the cap is reached;
    /// the largest polygon is always kept. Deterministic: area descending,
    /// original order as tie-break.
    /// </summary>
    private static IReadOnlyList<GdsOutlinePolygon> DropSmallestPolygons(
        IReadOnlyList<GdsOutlinePolygon> polygons, int maxTotalPoints, out int droppedCount)
    {
        if (polygons.Count == 0)
        {
            // Only reachable with a negative point cap (0 points > cap) — nothing
            // to drop; indexing the sorted list below would throw instead.
            droppedCount = 0;
            return polygons;
        }

        var byArea = polygons
            .Select((Polygon, Index) => (Polygon, Index, Area: Math.Abs(SignedArea(Polygon.Points))))
            .OrderByDescending(entry => entry.Area)
            .ThenBy(entry => entry.Index)
            .ToList();

        var kept = new List<GdsOutlinePolygon>();
        int budget = Math.Max(maxTotalPoints, byArea[0].Polygon.Points.Count);
        int used = 0;
        foreach (var entry in byArea)
        {
            if (used + entry.Polygon.Points.Count > budget && kept.Count > 0)
                continue;
            kept.Add(entry.Polygon);
            used += entry.Polygon.Points.Count;
        }

        droppedCount = polygons.Count - kept.Count;
        // Restore original polygon order for a stable, library-order outline list.
        return polygons.Where(kept.Contains).ToList();
    }

    private static double SignedArea(IReadOnlyList<GdsOutlinePoint> points)
    {
        double area = 0;
        for (int i = 0; i + 1 < points.Count; i++)
            area += points[i].X * points[i + 1].Y - points[i + 1].X * points[i].Y;
        return area / 2.0;
    }

    /// <summary>
    /// Iterative Ramer-Douglas-Peucker over the ring treated as a polyline.
    /// The first/last anchor (the same point for GDS-closed rings) is always
    /// kept, so the ring stays closed.
    /// </summary>
    private static IReadOnlyList<GdsOutlinePoint> RamerDouglasPeucker(
        IReadOnlyList<GdsOutlinePoint> points, double toleranceUm)
    {
        if (points.Count <= 2)
            return points;

        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int Start, int End)>();
        stack.Push((0, points.Count - 1));

        while (stack.Count > 0)
        {
            var (start, end) = stack.Pop();
            double maxDistance = 0;
            int split = -1;
            for (int i = start + 1; i < end; i++)
            {
                double distance = PerpendicularDistance(points[i], points[start], points[end]);
                if (distance > maxDistance)
                {
                    maxDistance = distance;
                    split = i;
                }
            }
            if (split < 0 || maxDistance <= toleranceUm)
                continue;
            keep[split] = true;
            stack.Push((start, split));
            stack.Push((split, end));
        }

        var result = new List<GdsOutlinePoint>();
        for (int i = 0; i < points.Count; i++)
        {
            if (keep[i])
                result.Add(points[i]);
        }
        return result;
    }

    /// <summary>Distance from <paramref name="point"/> to the segment a–b.</summary>
    private static double PerpendicularDistance(
        GdsOutlinePoint point, GdsOutlinePoint a, GdsOutlinePoint b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double lengthSquared = dx * dx + dy * dy;
        if (lengthSquared == 0)
            return Math.Sqrt((point.X - a.X) * (point.X - a.X) + (point.Y - a.Y) * (point.Y - a.Y));

        double t = ((point.X - a.X) * dx + (point.Y - a.Y) * dy) / lengthSquared;
        t = Math.Clamp(t, 0, 1);
        double projX = a.X + t * dx;
        double projY = a.Y + t * dy;
        return Math.Sqrt((point.X - projX) * (point.X - projX) + (point.Y - projY) * (point.Y - projY));
    }
}
