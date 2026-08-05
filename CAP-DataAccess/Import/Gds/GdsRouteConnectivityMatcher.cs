namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// The outcome of <see cref="GdsRouteConnectivityMatcher.Match"/>: the
/// route-derived pin pairs, which polygons they consumed, and which pins they
/// used up — the abutment matcher runs afterwards over the remaining pins.
/// </summary>
internal sealed record GdsRouteConnectivityResult
{
    /// <summary>Route-derived pairs in polygon scan order (<see cref="GdsPinPair.IsRouteDerived"/> set).</summary>
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
/// Derives connectivity from the routing structure itself: a top-cell
/// waveguide polygon was drawn TO connect pins, so the pins it touches tell us
/// what it connects. A polygon touched by EXACTLY two pins of different
/// instances (or one instance pin plus one top-cell port, consistent with the
/// abutment rules in <see cref="GdsAbutmentMatcher"/>) becomes a route-derived
/// <see cref="GdsPinPair"/> and the polygon is consumed (it is re-created as a
/// real, re-routable connection instead of a frozen path). Polygons touched by
/// 0/1 pins stay frozen paths; polygons touched by more than two pins are
/// junction/crossing topology — guessing the pairing there would miswire, so
/// they stay frozen with an informational note.
///
/// A pin "touches" a polygon when its point lies INSIDE the polygon (even-odd
/// point-in-polygon) or within the tolerance of its outline — the union of
/// both, not one or the other: route polygons are thin stripes whose end edge
/// sits on the pin, which outline distance catches (including sub-µm offsets
/// from PDK cell swaps or grid rounding), while a pin deep inside a fat
/// junction body is far from every edge and only point-in-polygon sees it.
/// Self-intersecting polygons do not occur in practice from nazca/gdsfactory
/// route output, so even-odd parity is well-defined.
///
/// One partner per pin, first polygon in scan order wins: a pin already
/// consumed by an earlier pair is invisible to later polygons (deterministic
/// in GDS element order).
/// </summary>
internal static class GdsRouteConnectivityMatcher
{
    /// <summary>A pin touching the current polygon: an instance pin, or a top-cell port when <see cref="IsPort"/>.</summary>
    private readonly record struct Touch(int InstanceIndex, int PinIndex, bool IsPort, GdsAbsolutePin Pin);

    /// <summary>
    /// Scans the polygons in order and derives a pair from every polygon
    /// touched by exactly two connectable pins. Pins are scanned in the same
    /// deterministic order the abutment matcher uses (instance placement
    /// order, then pin order; top-cell ports last).
    /// </summary>
    /// <param name="polygons">Top-cell waveguide polygons in app space.</param>
    /// <param name="pinsPerInstance">Absolute pins per instance index.</param>
    /// <param name="topPortPins">Absolute pins of the top cell's own port labels.</param>
    /// <param name="toleranceUm">Pin-to-polygon touch tolerance in micrometers.</param>
    /// <param name="infos">Collects the junction notes (polygons with &gt;2 pins).</param>
    public static GdsRouteConnectivityResult Match(
        IReadOnlyList<GdsOutlinePolygon> polygons,
        IReadOnlyList<IReadOnlyList<GdsAbsolutePin>> pinsPerInstance,
        IReadOnlyList<GdsAbsolutePin> topPortPins,
        double toleranceUm,
        List<string> infos)
    {
        var pairs = new List<GdsPinPair>();
        var consumedPolygons = new HashSet<int>();
        var consumedInstancePins = new HashSet<(int, int)>();
        var consumedPorts = new HashSet<int>();

        for (int p = 0; p < polygons.Count; p++)
        {
            var polygon = polygons[p];
            var touches = new List<Touch>();

            for (int i = 0; i < pinsPerInstance.Count; i++)
            {
                for (int k = 0; k < pinsPerInstance[i].Count; k++)
                {
                    if (!consumedInstancePins.Contains((i, k))
                        && Touches(polygon, pinsPerInstance[i][k], toleranceUm))
                    {
                        touches.Add(new Touch(i, k, IsPort: false, pinsPerInstance[i][k]));
                    }
                }
            }
            for (int t = 0; t < topPortPins.Count; t++)
            {
                if (!consumedPorts.Contains(t) && Touches(polygon, topPortPins[t], toleranceUm))
                {
                    touches.Add(new Touch(-1, t, IsPort: true, topPortPins[t]));
                }
            }

            if (touches.Count != 2)
            {
                if (touches.Count > 2)
                {
                    infos.Add(
                        $"Top-cell route polygon #{p + 1}: junction polygon with {touches.Count} pins " +
                        "left as frozen path (v1).");
                }
                continue;
            }

            var a = touches[0];
            var b = touches[1];
            if (a.IsPort && b.IsPort)
            {
                continue; // two external ports — nothing on the canvas to connect (v1).
            }
            if (!a.IsPort && !b.IsPort && a.InstanceIndex == b.InstanceIndex)
            {
                continue; // both pins of the same instance — a connection needs two partners.
            }

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
