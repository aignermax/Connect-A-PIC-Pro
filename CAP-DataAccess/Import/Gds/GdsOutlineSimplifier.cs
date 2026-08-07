namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// Simplifies draft outline polygons with the Ramer-Douglas-Peucker algorithm
/// and enforces the per-cell point cap: when the simplified polygons still
/// exceed the cap, the tolerance is raised adaptively (×4 per round, up to 8
/// rounds); as a last resort the smallest-area polygons are dropped (and
/// counted). A polygon that would collapse below the ring minimum at a raised
/// tolerance keeps its last valid simplification level instead of vanishing,
/// so the drop count is the ONLY removal and is always exact.
/// </summary>
internal static class GdsOutlineSimplifier
{
    /// <summary>
    /// Simplifies app-space outline polygons. <paramref name="droppedPolygonCount"/>
    /// reports how many polygons were dropped to satisfy the cap (0 = none) and is
    /// always exact: polygons are never silently removed — one whose simplification
    /// collapses below the 4-point ring minimum (3 distinct points + closing point)
    /// keeps its last valid level, falling back to its original ring when even the
    /// base tolerance collapses it. Only <see cref="DropSmallestPolygons"/> removes
    /// polygons, and it counts every one.
    /// </summary>
    public static IReadOnlyList<GdsOutlinePolygon> Simplify(
        IReadOnlyList<GdsOutlinePolygon> polygons,
        double toleranceUm,
        int maxTotalPoints,
        out int droppedPolygonCount)
    {
        droppedPolygonCount = 0;

        // Per-polygon working level, starting at the ORIGINAL ring: each round
        // re-simplifies from the original and adopts the result only when it is
        // still a valid ring, so one overshooting tolerance round can never wipe
        // out thousands of small polygons in a single step.
        var refined = new List<GdsOutlinePolygon>(polygons);
        RefineAll(polygons, refined, Math.Max(0, toleranceUm));
        double grownTolerance = Math.Max(toleranceUm, 1e-6);
        for (int round = 0; round < 8 && TotalPoints(refined) > maxTotalPoints; round++)
        {
            grownTolerance *= 4;
            RefineAll(polygons, refined, grownTolerance);
        }

        IReadOnlyList<GdsOutlinePolygon> current = refined;
        if (TotalPoints(current) > maxTotalPoints)
        {
            current = DropSmallestPolygons(current, maxTotalPoints, out droppedPolygonCount);
        }
        return current;
    }

    /// <summary>
    /// Re-simplifies every polygon from its ORIGINAL ring at
    /// <paramref name="toleranceUm"/> and adopts the result as the polygon's new
    /// working level — unless the ring would collapse below the 4-point minimum,
    /// in which case the polygon keeps its previous level: the collapse means the
    /// tolerance has overshot the polygon's size, not that the polygon is noise,
    /// and removing it would silently destroy geometry.
    /// </summary>
    private static void RefineAll(
        IReadOnlyList<GdsOutlinePolygon> originals,
        List<GdsOutlinePolygon> current,
        double toleranceUm)
    {
        for (int i = 0; i < originals.Count; i++)
        {
            var points = RamerDouglasPeucker(originals[i].Points, toleranceUm);
            if (points.Count >= 4)
                current[i] = originals[i] with { Points = points };
        }
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
