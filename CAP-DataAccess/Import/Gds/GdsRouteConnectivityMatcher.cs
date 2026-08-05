namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// The outcome of <see cref="GdsRouteConnectivityMatcher.Match"/>: the
/// route-derived pin pairs, which polygons they consumed, and which pins they
/// used up — the abutment matcher runs afterwards over the remaining pins.
/// </summary>
internal sealed record GdsRouteConnectivityResult
{
    /// <summary>Route-derived pairs in network scan order (<see cref="GdsPinPair.IsRouteDerived"/> set).</summary>
    public IReadOnlyList<GdsPinPair> Pairs { get; init; } = Array.Empty<GdsPinPair>();

    /// <summary>Indexes into the input polygon list that became connections (everything else stays a frozen path).</summary>
    public IReadOnlySet<int> ConsumedPolygonIndexes { get; init; } = new HashSet<int>();

    /// <summary>Instance pins used by the route-derived pairs (instance index, pin index) — excluded from abutment matching.</summary>
    public IReadOnlySet<(int InstanceIndex, int PinIndex)> ConsumedInstancePins { get; init; } =
        new HashSet<(int, int)>();

    /// <summary>Top-cell port indexes used by the route-derived pairs — excluded from abutment matching.</summary>
    public IReadOnlySet<int> ConsumedPortIndexes { get; init; } = new HashSet<int>();
}

/// <summary>
/// Derives connectivity from the routing structure itself: top-cell route
/// polygons were drawn TO connect pins, so the pins they touch tell us what
/// they connect. Because a drawn route flattens into a CHAIN of polygons (one
/// per emitted straight/bend segment, each touching only its neighbours, not
/// the pins at the far ends), the matcher first merges the polygons into
/// NETWORKS by transitive polygon∩polygon touch (union-find), then applies
/// the pin rule per network: a network touched by EXACTLY two pins (of two
/// different instances, of ONE instance — a drawn feedback loop coupling the
/// instance's own out back to its own in — or one instance pin plus one
/// top-cell port, consistent with the abutment rules in
/// <see cref="GdsAbutmentMatcher"/>) becomes one route-derived
/// <see cref="GdsPinPair"/> and all its polygons are consumed (re-created as a
/// real, re-routable connection instead of frozen paths).
/// Networks touched by 0/1 pins stay frozen paths; networks touched by more
/// than two pins are junction/crossing topology — guessing the pairing there
/// would miswire, so they stay frozen with an informational note.
///
/// A pin "touches" a polygon when its point lies INSIDE the polygon (even-odd
/// point-in-polygon) or within the tolerance of its outline — the union of
/// both, not one or the other: route polygons are thin stripes whose end edge
/// sits on the pin, which outline distance catches (including sub-µm offsets
/// from PDK cell swaps or grid rounding), while a pin deep inside a fat
/// junction body is far from every edge and only point-in-polygon sees it.
/// Two polygons touch when any outline segment pair comes within the
/// tolerance (segment intersection included) or a vertex of one lies inside
/// the other — consecutive route segments share their joint within export
/// rounding, and genuinely crossing routes merge into one junction network on
/// purpose. Self-intersecting polygons do not occur in practice from
/// nazca/gdsfactory route output, so even-odd parity is well-defined.
///
/// Metal (electrical) runs (<paramref name="electrical"/>): the same network
/// rule applies, but a network whose two pins are both KNOWN-optical (pins of
/// resolved PDK components, whose kinds are authoritative) is a contradiction —
/// metal only carries electrical signals — and stays frozen with a note.
/// Geometry-detected pins carry no kind (<see cref="GdsAbsolutePin.IsElectrical"/>
/// null), so they pair and get INFERRED as electrical by the caller.
///
/// One partner per pin, first network in scan order wins: a pin already
/// consumed by an earlier network is invisible to later networks
/// (deterministic in GDS element order).
/// </summary>
internal static class GdsRouteConnectivityMatcher
{
    /// <summary>A pin touching the current network: an instance pin, or a top-cell port when <see cref="IsPort"/>.</summary>
    private readonly record struct Touch(int InstanceIndex, int PinIndex, bool IsPort, GdsAbsolutePin Pin);

    /// <summary>
    /// Merges the polygons into touch networks, then scans the networks in
    /// order of their first polygon and derives a pair from every network
    /// touched by exactly two connectable pins. Pins are scanned in the same
    /// deterministic order the abutment matcher uses (instance placement
    /// order, then pin order; top-cell ports last).
    /// </summary>
    /// <param name="polygons">Top-cell route polygons in app space (one layer class only).</param>
    /// <param name="pinsPerInstance">Absolute pins per instance index.</param>
    /// <param name="topPortPins">Absolute pins of the top cell's own port labels.</param>
    /// <param name="toleranceUm">
    /// Pin-to-polygon touch tolerance in micrometers.
    /// </param>
    /// <param name="chainToleranceUm">
    /// Polygon-to-polygon chaining tolerance in micrometers — deliberately much
    /// tighter than the pin tolerance (see
    /// <see cref="GdsHierarchyImportOptions.PolygonChainToleranceUm"/>).
    /// </param>
    /// <param name="infos">Collects the junction notes (networks with &gt;2 pins).</param>
    /// <param name="electrical">
    /// True for a METAL-layer run: pairs are flagged <see cref="GdsPinPair.IsElectrical"/>
    /// and networks bridging two known-optical pins stay frozen (with a note).
    /// </param>
    /// <param name="preConsumedInstancePins">
    /// Instance pins an earlier run (the waveguide pass) already paired —
    /// seeded into the consumed set so one pin never gets two partners; the
    /// result's consumed sets INCLUDE them (union), ready to forward to the
    /// abutment matcher.
    /// </param>
    /// <param name="preConsumedPortIndexes">Top-cell ports an earlier run already paired.</param>
    public static GdsRouteConnectivityResult Match(
        IReadOnlyList<GdsOutlinePolygon> polygons,
        IReadOnlyList<IReadOnlyList<GdsAbsolutePin>> pinsPerInstance,
        IReadOnlyList<GdsAbsolutePin> topPortPins,
        double toleranceUm,
        double chainToleranceUm,
        List<string> infos,
        bool electrical = false,
        IReadOnlySet<(int InstanceIndex, int PinIndex)>? preConsumedInstancePins = null,
        IReadOnlySet<int>? preConsumedPortIndexes = null)
    {
        var pairs = new List<GdsPinPair>();
        var consumedPolygons = new HashSet<int>();
        var consumedInstancePins = new HashSet<(int, int)>(preConsumedInstancePins ?? Enumerable.Empty<(int, int)>());
        var consumedPorts = new HashSet<int>(preConsumedPortIndexes ?? Enumerable.Empty<int>());

        foreach (var network in BuildNetworks(polygons, chainToleranceUm))
        {
            var touches = new List<Touch>();
            var seen = new HashSet<(int, int)>();
            foreach (int p in network)
            {
                var polygon = polygons[p];
                for (int i = 0; i < pinsPerInstance.Count; i++)
                {
                    for (int k = 0; k < pinsPerInstance[i].Count; k++)
                    {
                        // `seen` marks pins that already TOUCHED an earlier polygon of this
                        // network (a pin counts once per network) — never as a visit marker:
                        // a pin may legitimately touch only the last polygon of a chain.
                        if (!consumedInstancePins.Contains((i, k))
                            && !seen.Contains((i, k))
                            && Touches(polygon, pinsPerInstance[i][k], toleranceUm))
                        {
                            seen.Add((i, k));
                            touches.Add(new Touch(i, k, IsPort: false, pinsPerInstance[i][k]));
                        }
                    }
                }
                for (int t = 0; t < topPortPins.Count; t++)
                {
                    if (!consumedPorts.Contains(t)
                        && !seen.Contains((-1, t))
                        && Touches(polygon, topPortPins[t], toleranceUm))
                    {
                        seen.Add((-1, t));
                        touches.Add(new Touch(-1, t, IsPort: true, topPortPins[t]));
                    }
                }
            }

            if (touches.Count != 2)
            {
                if (touches.Count > 2)
                {
                    var pinNames = string.Join(", ", touches.Select(t => $"'{t.Pin.Name}'"));
                    infos.Add(
                        $"Top-cell route network of {network.Count} polygon(s): junction with " +
                        $"{touches.Count} pins ({pinNames}) left as frozen path (v1).");
                }
                continue;
            }

            var a = touches[0];
            var b = touches[1];
            if (a.IsPort && b.IsPort)
            {
                continue; // two external ports — nothing on the canvas to connect (v1).
            }
            // Two pins of the SAME instance DO pair: a drawn route touching
            // exactly the instance's own two pins is a feedback loop (ring
            // self-coupling, black-box self-connection) — restore it.
            if (electrical && a.Pin.IsElectrical == false && b.Pin.IsElectrical == false)
            {
                // Metal-layer geometry between two KNOWN-optical pins contradicts the
                // signal domains — metal only carries electrical signals. Detected
                // pins (kind unknown, null) are allowed and inferred electrical.
                infos.Add(
                    $"Top-cell metal network of {network.Count} polygon(s) touches two optical " +
                    $"pins ('{a.Pin.Name}', '{b.Pin.Name}') — left as frozen path.");
                continue;
            }

            foreach (int p in network)
                consumedPolygons.Add(p);
            Consume(a);
            Consume(b);
            pairs.Add(new GdsPinPair
            {
                A = new GdsPinEndpoint { InstanceIndex = a.InstanceIndex, PinName = a.Pin.Name },
                B = new GdsPinEndpoint { InstanceIndex = b.InstanceIndex, PinName = b.Pin.Name },
                XUm = (a.Pin.XUm + b.Pin.XUm) / 2.0,
                YUm = (a.Pin.YUm + b.Pin.YUm) / 2.0,
                IsRouteDerived = true,
                IsElectrical = electrical,
            });
        }

        return new GdsRouteConnectivityResult
        {
            Pairs = pairs,
            ConsumedPolygonIndexes = consumedPolygons,
            ConsumedInstancePins = consumedInstancePins,
            ConsumedPortIndexes = consumedPorts,
        };

        void Consume(Touch touch)
        {
            if (touch.IsPort)
                consumedPorts.Add(touch.PinIndex);
            else
                consumedInstancePins.Add((touch.InstanceIndex, touch.PinIndex));
        }
    }

    /// <summary>
    /// Merges the polygons into networks of transitively touching polygons
    /// (union-find), returned in order of each network's first polygon index
    /// (member lists ascending). Deterministic in GDS element order.
    /// </summary>
    private static List<List<int>> BuildNetworks(
        IReadOnlyList<GdsOutlinePolygon> polygons, double toleranceUm)
    {
        var parent = new int[polygons.Count];
        for (int i = 0; i < parent.Length; i++)
            parent[i] = i;

        for (int p = 0; p < polygons.Count; p++)
        {
            for (int q = p + 1; q < polygons.Count; q++)
            {
                if (Find(p) != Find(q) && PolygonsTouch(polygons[p], polygons[q], toleranceUm))
                    Union(p, q);
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

    /// <summary>
    /// True when the pin point lies inside the polygon (even-odd rule) or
    /// within <paramref name="toleranceUm"/> of any outline segment.
    /// </summary>
    private static bool Touches(GdsOutlinePolygon polygon, GdsAbsolutePin pin, double toleranceUm)
    {
        var points = polygon.Points;
        if (points.Count == 0)
            return false;
        if (PointInPolygon(points, pin.XUm, pin.YUm))
            return true;

        double toleranceSquared = toleranceUm * toleranceUm;
        for (int i = 0; i < points.Count; i++)
        {
            if (DistanceToSegmentSquared(pin.XUm, pin.YUm, points[i], points[(i + 1) % points.Count])
                <= toleranceSquared)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Even-odd point-in-polygon (ray cast towards +X; boundary hits count as inside via the outline distance).</summary>
    private static bool PointInPolygon(IReadOnlyList<GdsOutlinePoint> polygon, double x, double y)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var pi = polygon[i];
            var pj = polygon[j];
            if ((pi.Y > y) != (pj.Y > y)
                && x < ((pj.X - pi.X) * (y - pi.Y) / (pj.Y - pi.Y)) + pi.X)
            {
                inside = !inside;
            }
        }
        return inside;
    }

    private static double DistanceToSegmentSquared(
        double px, double py, GdsOutlinePoint a, GdsOutlinePoint b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double lengthSquared = (dx * dx) + (dy * dy);
        double t = lengthSquared == 0
            ? 0
            : Math.Clamp(((px - a.X) * dx + (py - a.Y) * dy) / lengthSquared, 0, 1);
        double cx = a.X + (t * dx) - px;
        double cy = a.Y + (t * dy) - py;
        return (cx * cx) + (cy * cy);
    }
}
