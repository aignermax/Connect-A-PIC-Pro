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
/// One exception to the junction rule: Lunima's own export stamps a top-cell
/// port label exactly on every coupler pin, so a coupler-terminated route
/// legitimately sees the label as a THIRD touch on the same joint. A port
/// touch coincident (within the touch tolerance) with an instance pin
/// already touching the network IS that joint, not a third party — it is
/// dropped from the pairing decision and stays unconsumed, so the design's
/// external port survives while the route reconnects.
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
///
/// Performance: both quadratic scans run over a <see cref="GdsSpatialGrid"/>
/// uniform hash — polygon∩polygon candidates come from bbox overlap for the
/// network build, pin candidates from the polygon's expanded bbox for the
/// touch scan. The grid only prunes candidates that geometrically cannot
/// qualify, so results are identical to the brute-force scans; what was
/// O(polygons²) + O(polygons × pins) becomes near-linear and a production-scale
/// file (thousands of route polygons and pins) imports in seconds instead of
/// hanging the dialog.
/// </summary>
internal static partial class GdsRouteConnectivityMatcher
{
    /// <summary>A pin touching the current network: an instance pin, or a top-cell port when <see cref="IsPort"/>.</summary>
    private readonly record struct Touch(int InstanceIndex, int PinIndex, bool IsPort, GdsAbsolutePin Pin);

    /// <summary>Axis-aligned polygon bbox; <see cref="IsEmpty"/> marks polygons with no points (they never touch anything).</summary>
    private readonly record struct Bounds(double MinX, double MinY, double MaxX, double MaxY)
    {
        public bool IsEmpty => MaxX < MinX;
    }

    /// <summary>
    /// The pins in one flat deterministic scan order — all instance pins
    /// (instance placement order, then pin order), then the top-cell ports —
    /// with a spatial index over their positions. The ordinal IS the scan order:
    /// sorting grid candidates by it reproduces the brute-force nested loops
    /// exactly.
    /// </summary>
    private sealed class PinTable
    {
        public List<int> InstanceOf = new(); // −1 for a top-cell port
        public List<int> IndexOf = new();    // pin index within the instance, or port index
        public GdsSpatialGrid? Grid;

        public int Count => InstanceOf.Count;
    }

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

        var bounds = ComputeBounds(polygons);
        var pinTable = BuildPinTable(polygons, pinsPerInstance, topPortPins, toleranceUm, bounds);

        foreach (var network in BuildNetworks(polygons, bounds, chainToleranceUm))
        {
            var touches = new List<Touch>();
            var seen = new HashSet<(int, int)>();
            foreach (int p in network)
            {
                var polygon = polygons[p];
                if (polygon.Points.Count == 0)
                    continue;
                var box = bounds[p];

                // `seen` marks pins that already TOUCHED an earlier polygon of this
                // network (a pin counts once per network) — never as a visit marker:
                // a pin may legitimately touch only the last polygon of a chain.
                // The grid candidates are unordered — the ordinal sort restores the
                // deterministic scan order (instance pins, then top-cell ports).
                var candidates = pinTable.Grid is null
                    ? Enumerable.Empty<int>()
                    : pinTable.Grid.QueryBox(
                        box.MinX - toleranceUm, box.MinY - toleranceUm,
                        box.MaxX + toleranceUm, box.MaxY + toleranceUm);
                foreach (int ordinal in candidates.OrderBy(o => o))
                {
                    int i = pinTable.InstanceOf[ordinal];
                    int k = pinTable.IndexOf[ordinal];
                    if (i < 0)
                    {
                        if (!consumedPorts.Contains(k)
                            && !seen.Contains((-1, k))
                            && Touches(polygon, topPortPins[k], toleranceUm))
                        {
                            seen.Add((-1, k));
                            touches.Add(new Touch(-1, k, IsPort: true, topPortPins[k]));
                        }
                    }
                    else if (!consumedInstancePins.Contains((i, k))
                             && !seen.Contains((i, k))
                             && Touches(polygon, pinsPerInstance[i][k], toleranceUm))
                    {
                        seen.Add((i, k));
                        touches.Add(new Touch(i, k, IsPort: false, pinsPerInstance[i][k]));
                    }
                }
            }

            // Own-export port labels sit exactly on the coupler pins, so a route
            // ending at a coupler sees the label as a third touch ON THE SAME
            // JOINT — the junction rule would freeze the honest 2-pin route. A
            // port coincident with an instance pin already touching the network
            // IS that joint, not a third party: drop it from the pairing
            // decision. The port itself stays unconsumed — it remains the
            // design's external port and is never matched here.
            if (touches.Count > 2)
            {
                var pinTouches = touches.Where(t => !t.IsPort).ToList();
                touches.RemoveAll(t => t.IsPort
                    && pinTouches.Any(p => IsCoincident(p.Pin, t.Pin, toleranceUm)));
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
                // The drawn geometry rides along so the placement layer can attach
                // it as the connection's frozen cached route instead of re-routing
                // (shares the polygon instances — records, never mutated).
                SourcePolygons = network.Select(p => polygons[p]).ToList(),
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

    /// <summary>Per-polygon bounding boxes; empty polygons get the empty marker (they never touch anything).</summary>
    private static Bounds[] ComputeBounds(IReadOnlyList<GdsOutlinePolygon> polygons)
    {
        var bounds = new Bounds[polygons.Count];
        for (var p = 0; p < polygons.Count; p++)
        {
            var points = polygons[p].Points;
            if (points.Count == 0)
            {
                bounds[p] = new Bounds(0, 0, -1, -1); // empty marker
                continue;
            }
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            foreach (var point in points)
            {
                minX = Math.Min(minX, point.X);
                minY = Math.Min(minY, point.Y);
                maxX = Math.Max(maxX, point.X);
                maxY = Math.Max(maxY, point.Y);
            }
            bounds[p] = new Bounds(minX, minY, maxX, maxY);
        }
        return bounds;
    }

    /// <summary>
    /// Builds the flat pin table with its spatial index; the grid is null when
    /// there are no pins to find (networks then simply collect no touches).
    /// The cell size adapts to the pin spread and the touch tolerance.
    /// </summary>
    private static PinTable BuildPinTable(
        IReadOnlyList<GdsOutlinePolygon> polygons,
        IReadOnlyList<IReadOnlyList<GdsAbsolutePin>> pinsPerInstance,
        IReadOnlyList<GdsAbsolutePin> topPortPins,
        double toleranceUm,
        Bounds[] bounds)
    {
        var table = new PinTable();
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        void Add(GdsAbsolutePin pin, int instanceIndex, int pinIndex)
        {
            table.InstanceOf.Add(instanceIndex);
            table.IndexOf.Add(pinIndex);
            minX = Math.Min(minX, pin.XUm);
            minY = Math.Min(minY, pin.YUm);
            maxX = Math.Max(maxX, pin.XUm);
            maxY = Math.Max(maxY, pin.YUm);
        }

        for (var i = 0; i < pinsPerInstance.Count; i++)
            for (var k = 0; k < pinsPerInstance[i].Count; k++)
                Add(pinsPerInstance[i][k], i, k);
        for (var t = 0; t < topPortPins.Count; t++)
            Add(topPortPins[t], -1, t);

        if (table.Count == 0 || polygons.Count == 0)
            return table;

        // The grid spans pins AND polygons alike so outlier geometry cannot
        // stretch the cells; the spread of either side alone could be tiny.
        for (var p = 0; p < bounds.Length; p++)
        {
            if (bounds[p].IsEmpty)
                continue;
            minX = Math.Min(minX, bounds[p].MinX);
            minY = Math.Min(minY, bounds[p].MinY);
            maxX = Math.Max(maxX, bounds[p].MaxX);
            maxY = Math.Max(maxY, bounds[p].MaxY);
        }
        var span = Math.Max(maxX - minX, maxY - minY);
        table.Grid = GdsSpatialGrid.Create(span, toleranceUm, table.Count);

        var ordinal = 0;
        for (var i = 0; i < pinsPerInstance.Count; i++)
            for (var k = 0; k < pinsPerInstance[i].Count; k++)
                table.Grid.InsertPoint(ordinal++, pinsPerInstance[i][k].XUm, pinsPerInstance[i][k].YUm);
        for (var t = 0; t < topPortPins.Count; t++)
            table.Grid.InsertPoint(ordinal++, topPortPins[t].XUm, topPortPins[t].YUm);
        return table;
    }
}
