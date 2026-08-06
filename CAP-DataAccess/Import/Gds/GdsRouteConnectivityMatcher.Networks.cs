namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// Network building for <see cref="GdsRouteConnectivityMatcher"/>: union-find
/// over transitively touching polygons, with the polygon-pair touch predicates.
/// </summary>
internal static partial class GdsRouteConnectivityMatcher
{
    /// <summary>
    /// Merges the polygons into networks of transitively touching polygons
    /// (union-find), returned in order of each network's first polygon index
    /// (member lists ascending). Deterministic in GDS element order. Candidate
    /// pairs come from a spatial grid over the polygon bboxes — only polygons
    /// whose bboxes come within the tolerance can touch, so the quadratic
    /// all-pairs scan is pruned to near-neighbours (connected components are
    /// order-independent: the networks are identical to the brute-force build).
    /// </summary>
    private static List<List<int>> BuildNetworks(
        IReadOnlyList<GdsOutlinePolygon> polygons, Bounds[] bounds, double toleranceUm)
    {
        var parent = new int[polygons.Count];
        for (int i = 0; i < parent.Length; i++)
            parent[i] = i;

        if (polygons.Count > 1)
        {
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            foreach (var box in bounds)
            {
                if (box.IsEmpty)
                    continue;
                minX = Math.Min(minX, box.MinX);
                minY = Math.Min(minY, box.MinY);
                maxX = Math.Max(maxX, box.MaxX);
                maxY = Math.Max(maxY, box.MaxY);
            }
            var span = Math.Max(maxX - minX, maxY - minY);
            var grid = GdsSpatialGrid.Create(span, toleranceUm, polygons.Count);
            for (var p = 0; p < polygons.Count; p++)
            {
                if (!bounds[p].IsEmpty)
                    grid.InsertBox(p, bounds[p].MinX, bounds[p].MinY, bounds[p].MaxX, bounds[p].MaxY);
            }

            for (var p = 0; p < polygons.Count; p++)
            {
                if (bounds[p].IsEmpty)
                    continue;
                // Raw bboxes are inserted; querying with the tolerance-expanded
                // box finds exactly the polygons whose bbox comes within the
                // tolerance (a superset of actual touches — PolygonsTouch decides).
                var candidates = grid.QueryBox(
                    bounds[p].MinX - toleranceUm, bounds[p].MinY - toleranceUm,
                    bounds[p].MaxX + toleranceUm, bounds[p].MaxY + toleranceUm);
                foreach (int q in candidates)
                {
                    if (q > p && Find(p) != Find(q) && PolygonsTouch(polygons[p], polygons[q], toleranceUm))
                        Union(p, q);
                }
            }
        }

        var networks = new Dictionary<int, List<int>>();
        for (int i = 0; i < polygons.Count; i++)
        {
            int root = Find(i);
            if (!networks.TryGetValue(root, out var members))
                networks.Add(root, members = new List<int>());
            members.Add(i);
        }
        return networks.Values
            .OrderBy(members => members[0])
            .ToList();

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]]; // path halving
                x = parent[x];
            }
            return x;
        }

        void Union(int x, int y) => parent[Find(x)] = Find(y);
    }

    /// <summary>
    /// True when the two polygons touch within <paramref name="toleranceUm"/>:
    /// any outline segment pair intersects or comes within the tolerance, or a
    /// vertex of either polygon lies inside the other.
    /// </summary>
    private static bool PolygonsTouch(GdsOutlinePolygon a, GdsOutlinePolygon b, double toleranceUm)
    {
        if (a.Points.Count == 0 || b.Points.Count == 0)
            return false;

        double toleranceSquared = toleranceUm * toleranceUm;
        for (int i = 0; i < a.Points.Count; i++)
        {
            var a1 = a.Points[i];
            var a2 = a.Points[(i + 1) % a.Points.Count];
            for (int j = 0; j < b.Points.Count; j++)
            {
                var b1 = b.Points[j];
                var b2 = b.Points[(j + 1) % b.Points.Count];
                if (SegmentsTouch(a1, a2, b1, b2, toleranceSquared))
                    return true;
            }
        }

        return PointInPolygon(b.Points, a.Points[0].X, a.Points[0].Y)
            || PointInPolygon(a.Points, b.Points[0].X, b.Points[0].Y);
    }

    /// <summary>
    /// True when segments a1–a2 and b1–b2 intersect (a proper crossing has
    /// distance zero at the crossing point, which endpoint distances alone
    /// never see) or come within the tolerance (squared).
    /// </summary>
    private static bool SegmentsTouch(
        GdsOutlinePoint a1, GdsOutlinePoint a2, GdsOutlinePoint b1, GdsOutlinePoint b2,
        double toleranceSquared)
    {
        if (SegmentsIntersect(a1, a2, b1, b2))
            return true;
        return DistanceToSegmentSquared(a1.X, a1.Y, b1, b2) <= toleranceSquared
            || DistanceToSegmentSquared(a2.X, a2.Y, b1, b2) <= toleranceSquared
            || DistanceToSegmentSquared(b1.X, b1.Y, a1, a2) <= toleranceSquared
            || DistanceToSegmentSquared(b2.X, b2.Y, a1, a2) <= toleranceSquared;
    }

    /// <summary>Standard orientation-test segment intersection (proper crossings only).</summary>
    private static bool SegmentsIntersect(
        GdsOutlinePoint a1, GdsOutlinePoint a2, GdsOutlinePoint b1, GdsOutlinePoint b2)
    {
        double d1 = Cross(b1, b2, a1);
        double d2 = Cross(b1, b2, a2);
        double d3 = Cross(a1, a2, b1);
        double d4 = Cross(a1, a2, b2);
        if (d1 == 0 || d2 == 0 || d3 == 0 || d4 == 0)
            return false; // collinear or endpoint contact — the distance checks in SegmentsTouch cover those
        return (d1 > 0) != (d2 > 0) && (d3 > 0) != (d4 > 0);
    }

    private static double Cross(GdsOutlinePoint o, GdsOutlinePoint a, GdsOutlinePoint b) =>
        (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
}
