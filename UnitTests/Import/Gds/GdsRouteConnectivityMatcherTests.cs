using CAP_DataAccess.Import.Gds;
using Shouldly;

namespace UnitTests.Import.Gds;

/// <summary>
/// Unit tests for <see cref="GdsRouteConnectivityMatcher"/> with hand-built
/// polygons/pins: transitive chain merging into networks, the exactly-two-pins
/// rule per network (including same-instance feedback loops), junction/crossing
/// networks, the two-port exclusion, one-partner-per-pin consumption, the
/// metal (electrical) run rules, and the touch test itself
/// (point-in-polygon ∪ outline distance).
///
/// The matcher never looks at layers: restricting the input to waveguide- or
/// metal-layer polygons happens upstream (<c>GdsHierarchyImportSession</c>
/// filters by <c>GdsHierarchyImportOptions.RouteLayers</c> /
/// <c>MetalRouteLayers</c>), so every fixture below only feeds polygons that
/// already passed that filter.
/// </summary>
public class GdsRouteConnectivityMatcherTests
{
    private const double Tol = 1.0;

    /// <summary>Chaining tolerance (polygon∩polygon) — mirrors the production default (0.05 µm).</summary>
    private const double ChainTol = 0.05;

    /// <summary>Closed axis-aligned rectangle ring (5 points, first repeated), like the importer produces.</summary>
    private static GdsOutlinePolygon Rect(double x1, double y1, double x2, double y2) =>
        new()
        {
            Layer = 1,
            DataType = 0,
            Points = new[]
            {
                new GdsOutlinePoint(x1, y1),
                new GdsOutlinePoint(x2, y1),
                new GdsOutlinePoint(x2, y2),
                new GdsOutlinePoint(x1, y2),
                new GdsOutlinePoint(x1, y1),
            },
        };

    /// <summary>The 5 µm bridge shape of the importer fixture: x ∈ [10, 15], y ∈ [1.75, 2.25].</summary>
    private static GdsOutlinePolygon Bridge() => Rect(10, 1.75, 15, 2.25);

    private static GdsAbsolutePin Pin(string name, double x, double y) =>
        new() { Name = name, XUm = x, YUm = y };

    private static GdsRouteConnectivityResult Match(
        IReadOnlyList<GdsOutlinePolygon> polygons,
        IReadOnlyList<IReadOnlyList<GdsAbsolutePin>>? pinsPerInstance = null,
        IReadOnlyList<GdsAbsolutePin>? topPortPins = null,
        List<string>? infos = null,
        bool electrical = false) =>
        GdsRouteConnectivityMatcher.Match(
            polygons,
            pinsPerInstance ?? Array.Empty<IReadOnlyList<GdsAbsolutePin>>(),
            topPortPins ?? Array.Empty<GdsAbsolutePin>(),
            Tol,
            ChainTol,
            infos ?? new List<string>(),
            electrical);

    [Fact]
    public void Match_PolygonBridgingTwoInstancePins_BecomesRouteDerivedPair()
    {
        // wgA.out at (10, 2) sits on the bridge's left edge, wgB.in at (15, 2)
        // on its right edge — the drawn route IS the connection.
        var pinsPerInstance = new IReadOnlyList<GdsAbsolutePin>[]
        {
            new[] { Pin("in", 0, 2), Pin("out", 10, 2) },
            new[] { Pin("in", 15, 2), Pin("out", 25, 2) },
        };
        var infos = new List<string>();

        var result = Match(new[] { Bridge() }, pinsPerInstance, infos: infos);

        var pair = result.Pairs.ShouldHaveSingleItem();
        pair.IsRouteDerived.ShouldBeTrue();
        pair.A.InstanceIndex.ShouldBe(0);
        pair.A.PinName.ShouldBe("out");
        pair.B.InstanceIndex.ShouldBe(1);
        pair.B.PinName.ShouldBe("in");
        pair.XUm.ShouldBe(12.5, 1e-9);
        pair.YUm.ShouldBe(2.0, 1e-9);

        result.ConsumedPolygonIndexes.ShouldBe(new[] { 0 });
        result.ConsumedInstancePins.ShouldBe(new[] { (0, 1), (1, 0) }, ignoreOrder: true);
        result.ConsumedPortIndexes.ShouldBeEmpty();
        infos.ShouldBeEmpty();
    }

    [Fact]
    public void Match_PolygonTouchingOnePin_StaysFrozen()
    {
        var pinsPerInstance = new IReadOnlyList<GdsAbsolutePin>[]
        {
            new[] { Pin("in", 0, 2), Pin("out", 10, 2) }, // only "out" touches the bridge
        };

        var result = Match(new[] { Bridge() }, pinsPerInstance);

        result.Pairs.ShouldBeEmpty("a route to exactly ONE pin connects nothing");
        result.ConsumedPolygonIndexes.ShouldBeEmpty();
        result.ConsumedInstancePins.ShouldBeEmpty();
    }

    [Fact]
    public void Match_PolygonTouchingThreePins_StaysFrozenWithJunctionNote()
    {
        // Third pin dead-center inside the bridge: junction/crossing topology —
        // guessing the pairing would miswire, so it stays frozen with a note.
        var pinsPerInstance = new IReadOnlyList<GdsAbsolutePin>[]
        {
            new[] { Pin("out", 10, 2) },
            new[] { Pin("in", 15, 2) },
            new[] { Pin("tap", 12.5, 2) },
        };
        var infos = new List<string>();

        var result = Match(new[] { Bridge() }, pinsPerInstance, infos: infos);

        result.Pairs.ShouldBeEmpty();
        result.ConsumedPolygonIndexes.ShouldBeEmpty();
        result.ConsumedInstancePins.ShouldBeEmpty("junction pins stay available for abutment matching");
        infos.ShouldHaveSingleItem().ShouldContain("junction with 3 pins");
    }

    [Fact]
    public void Match_PolygonBridgingTwoPinsOfSameInstance_BecomesFeedbackLoopPair()
    {
        // One polygon bridging BOTH pins of instance 0 — a drawn feedback loop
        // (ring self-coupling, black-box self-connection): it must become a
        // route-derived pair and consume the pins like any other connection.
        var pinsPerInstance = new IReadOnlyList<GdsAbsolutePin>[]
        {
            new[] { Pin("in", 10, 2), Pin("out", 15, 2) },
            new[] { Pin("in", 40, 2), Pin("out", 50, 2) },
        };

        var result = Match(new[] { Bridge() }, pinsPerInstance);

        var pair = result.Pairs.ShouldHaveSingleItem();
        pair.IsRouteDerived.ShouldBeTrue();
        pair.A.InstanceIndex.ShouldBe(0);
        pair.A.PinName.ShouldBe("in");
        pair.B.InstanceIndex.ShouldBe(0, "a feedback loop connects the instance to itself");
        pair.B.PinName.ShouldBe("out");
        result.ConsumedPolygonIndexes.ShouldBe(new[] { 0 });
        result.ConsumedInstancePins.ShouldBe(new[] { (0, 0), (0, 1) }, ignoreOrder: true);
    }

    [Fact]
    public void Match_PolygonBridgingInstancePinAndTopPort_PairsWithPortEndpoint()
    {
        var pinsPerInstance = new IReadOnlyList<GdsAbsolutePin>[]
        {
            new[] { Pin("in", 0, 2), Pin("out", 10, 2) },
        };
        var topPortPins = new[] { Pin("o1", 15, 2) };

        var result = Match(new[] { Bridge() }, pinsPerInstance, topPortPins);

        var pair = result.Pairs.ShouldHaveSingleItem();
        pair.IsRouteDerived.ShouldBeTrue();
        pair.A.InstanceIndex.ShouldBe(0);
        pair.A.PinName.ShouldBe("out");
        pair.B.InstanceIndex.ShouldBe(-1, "the port endpoint carries InstanceIndex −1");
        pair.B.IsTopLevelPort.ShouldBeTrue();
        pair.B.PinName.ShouldBe("o1");
        result.ConsumedPolygonIndexes.ShouldBe(new[] { 0 });
        result.ConsumedInstancePins.ShouldBe(new[] { (0, 1) });
        result.ConsumedPortIndexes.ShouldBe(new[] { 0 });
    }

    [Fact]
    public void Match_PolygonBridgingTwoTopPorts_NoPair()
    {
        // Two external ports — nothing on the canvas to connect (v1). The ports
        // stay unconsumed and available for the abutment matcher.
        var topPortPins = new[] { Pin("o1", 10, 2), Pin("o2", 15, 2) };

        var result = Match(new[] { Bridge() }, topPortPins: topPortPins);

        result.Pairs.ShouldBeEmpty();
        result.ConsumedPolygonIndexes.ShouldBeEmpty();
        result.ConsumedPortIndexes.ShouldBeEmpty();
    }

    [Fact]
    public void Match_SegmentedChainMergesIntoOneNetwork_OnePairConsumesAllPolygons()
    {
        // A drawn route flattens into a CHAIN of polygons (one per segment):
        // only the first touches the start pin, only the last the end pin.
        // Transitive merging must restore ONE connection and consume all three.
        var pinsPerInstance = new IReadOnlyList<GdsAbsolutePin>[]
        {
            new[] { Pin("out", 10, 2) },
            new[] { Pin("in", 40, 17) },
        };
        var chain = new[]
        {
            Rect(10, 1.75, 20, 2.25),      // horizontal from the start pin
            Rect(19.75, 2, 20.25, 17),     // vertical riser, overlapping the first
            Rect(20, 16.75, 40, 17.25),    // horizontal into the end pin
        };

        var result = Match(chain, pinsPerInstance);

        var pair = result.Pairs.ShouldHaveSingleItem("the chain is one network with exactly two pins");
        pair.A.PinName.ShouldBe("out");
        pair.B.PinName.ShouldBe("in");
        pair.IsElectrical.ShouldBeFalse();
        result.ConsumedPolygonIndexes.ShouldBe(new[] { 0, 1, 2 }, ignoreOrder: true);
        result.ConsumedInstancePins.ShouldBe(new[] { (0, 0), (1, 0) }, ignoreOrder: true);
    }

    [Fact]
    public void Match_CrossingChainsMergeIntoOneJunctionNetwork_StaysFrozen()
    {
        // Two independent routes CROSS: their polygons touch, merging into one
        // 4-pin network — crossing-vs-junction pairing is guesswork, so the
        // whole network stays frozen with ONE junction note.
        var pinsPerInstance = new IReadOnlyList<GdsAbsolutePin>[]
        {
            new[] { Pin("out", 0, 10) },
            new[] { Pin("in", 30, 10) },
            new[] { Pin("out", 15, 0) },
            new[] { Pin("in", 15, 30) },
        };
        var crossing = new[]
        {
            Rect(0, 9.75, 20, 10.25),   // horizontal route, part 1
            Rect(20, 9.75, 30, 10.25),  // horizontal route, part 2
            Rect(14.75, 0, 15.25, 30),  // vertical route crossing both parts
        };
        var infos = new List<string>();

        var result = Match(crossing, pinsPerInstance, infos: infos);

        result.Pairs.ShouldBeEmpty("a crossing network is junction topology — never guessed");
        result.ConsumedPolygonIndexes.ShouldBeEmpty();
        infos.ShouldHaveSingleItem().ShouldContain("junction with 4 pins");
    }

    [Fact]
    public void Match_TwoDisjointNetworks_EachBecomesAPair()
    {
        // Two well-separated routes: independent networks, one pair each.
        var pinsPerInstance = new IReadOnlyList<GdsAbsolutePin>[]
        {
            new[] { Pin("out", 0, 2) },
            new[] { Pin("in", 10, 2) },
            new[] { Pin("out", 0, 20) },
            new[] { Pin("in", 10, 20) },
        };
        var polygons = new[] { Rect(0, 1.75, 10, 2.25), Rect(0, 19.75, 10, 20.25) };

        var result = Match(polygons, pinsPerInstance);

        result.Pairs.Count.ShouldBe(2);
        result.ConsumedPolygonIndexes.ShouldBe(new[] { 0, 1 }, ignoreOrder: true);
    }

    [Fact]
    public void Match_MetalNetworkBetweenElectricalPins_ElectricalPair()
    {
        var pinsPerInstance = new IReadOnlyList<GdsAbsolutePin>[]
        {
            new[] { new GdsAbsolutePin { Name = "anode", XUm = 10, YUm = 2, IsElectrical = true } },
            new[] { new GdsAbsolutePin { Name = "elec", XUm = 15, YUm = 2, IsElectrical = true } },
        };

        var result = Match(new[] { Bridge() }, pinsPerInstance, electrical: true);

        var pair = result.Pairs.ShouldHaveSingleItem();
        pair.IsRouteDerived.ShouldBeTrue();
        pair.IsElectrical.ShouldBeTrue("a metal-layer network derives an electrical connection");
        result.ConsumedPolygonIndexes.ShouldBe(new[] { 0 });
    }

    [Fact]
    public void Match_MetalNetworkBetweenUnknownKindPins_PairsForInference()
    {
        // Geometry-detected pins carry no kind (null): metal-layer evidence is
        // enough — the pair is derived and the caller infers the electrical domain.
        var pinsPerInstance = new IReadOnlyList<GdsAbsolutePin>[]
        {
            new[] { Pin("p1", 10, 2) },
            new[] { Pin("p2", 15, 2) },
        };

        var result = Match(new[] { Bridge() }, pinsPerInstance, electrical: true);

        result.Pairs.ShouldHaveSingleItem().IsElectrical.ShouldBeTrue();
    }

    [Fact]
    public void Match_MetalNetworkBetweenTwoKnownOpticalPins_StaysFrozenWithNote()
    {
        // Metal between two KNOWN-optical pins contradicts the signal domains —
        // never fabricate that connection; the network stays frozen with a note.
        var pinsPerInstance = new IReadOnlyList<GdsAbsolutePin>[]
        {
            new[] { new GdsAbsolutePin { Name = "a0", XUm = 10, YUm = 2, IsElectrical = false } },
            new[] { new GdsAbsolutePin { Name = "b0", XUm = 15, YUm = 2, IsElectrical = false } },
        };
        var infos = new List<string>();

        var result = Match(new[] { Bridge() }, pinsPerInstance, infos: infos, electrical: true);

        result.Pairs.ShouldBeEmpty();
        result.ConsumedPolygonIndexes.ShouldBeEmpty();
        infos.ShouldHaveSingleItem().ShouldContain("two optical pins");
    }

    [Fact]
    public void Match_ParallelTracesBelowPinTolerance_StaySeparateNetworks()
    {
        // Two fat parallel traces 0.6 µm apart (edge to edge) — a metal bus at
        // fine pitch. The pin-touch tolerance (1.0 µm) would bridge them, but
        // the chain tolerance (0.05 µm) must not: each trace is its own network
        // and pairs its own two pins.
        var pinsPerInstance = new IReadOnlyList<GdsAbsolutePin>[]
        {
            new[] { new GdsAbsolutePin { Name = "anode", XUm = 10, YUm = 2, IsElectrical = true } },
            new[] { new GdsAbsolutePin { Name = "elec_a", XUm = 40, YUm = 2, IsElectrical = true } },
            new[] { new GdsAbsolutePin { Name = "cathode", XUm = 10, YUm = 30, IsElectrical = true } },
            new[] { new GdsAbsolutePin { Name = "elec_b", XUm = 40, YUm = 30, IsElectrical = true } },
        };
        var bus = new[]
        {
            // 20 µm-wide traces: y ∈ [−8, 12] and y ∈ [12.6, 32.6] — 0.6 µm apart.
            Rect(10, -8, 40, 12),
            Rect(10, 12.6, 40, 32.6),
        };

        var result = Match(bus, pinsPerInstance, electrical: true);

        result.Pairs.Count.ShouldBe(2, "each trace pairs its own endpoints — the bus never merges");
        result.Pairs.ShouldAllBe(p => p.IsElectrical);
        result.ConsumedPolygonIndexes.ShouldBe(new[] { 0, 1 }, ignoreOrder: true);
    }

    [Fact]
    public void Match_PreConsumedPins_AreInvisibleToTheSecondRun()
    {
        // The waveguide pass consumed instance1.in: the metal run must not
        // re-pair it (its network then sees only one pin and stays frozen).
        var pinsPerInstance = new IReadOnlyList<GdsAbsolutePin>[]
        {
            new[] { Pin("out", 10, 2) },
            new[] { Pin("in", 15, 2) },
        };
        var preConsumed = new HashSet<(int, int)> { (1, 0) };

        var result = GdsRouteConnectivityMatcher.Match(
            new[] { Bridge() }, pinsPerInstance, Array.Empty<GdsAbsolutePin>(), Tol, ChainTol,
            new List<string>(), electrical: true, preConsumedInstancePins: preConsumed);

        result.Pairs.ShouldBeEmpty();
        result.ConsumedInstancePins.ShouldBe(new[] { (1, 0) }, ignoreOrder: true,
            customMessage: "the result's consumed set includes the pre-consumed pins (union, ready for abutment)");
    }

    [Fact]
    public void Match_PinWithinTouchTolerance_Pairs()
    {
        // 0.9·tol above the bridge's top edge (app y = 2.25 + 0.9): outline
        // distance catches sub-µm offsets from PDK cell swaps / grid rounding.
        var pinsPerInstance = new IReadOnlyList<GdsAbsolutePin>[]
        {
            new[] { Pin("out", 10, 2.25 + 0.9 * Tol) },
            new[] { Pin("in", 15, 2) },
        };

        var result = Match(new[] { Bridge() }, pinsPerInstance);

        result.Pairs.ShouldHaveSingleItem();
        result.ConsumedPolygonIndexes.ShouldBe(new[] { 0 });
    }

    [Fact]
    public void Match_PinBeyondTouchTolerance_DoesNotTouch()
    {
        // 1.1·tol above the top edge: outside the window — only the second pin
        // touches, and one pin is no pair.
        var pinsPerInstance = new IReadOnlyList<GdsAbsolutePin>[]
        {
            new[] { Pin("out", 10, 2.25 + 1.1 * Tol) },
            new[] { Pin("in", 15, 2) },
        };

        var result = Match(new[] { Bridge() }, pinsPerInstance);

        result.Pairs.ShouldBeEmpty();
        result.ConsumedPolygonIndexes.ShouldBeEmpty();
        result.ConsumedInstancePins.ShouldBeEmpty();
    }

    [Fact]
    public void Match_PinDeepInsideFatPolygon_TouchesViaPointInPolygon()
    {
        // A fat junction body: both pins sit 5 µm from every edge — far beyond
        // the outline tolerance, so only even-odd point-in-polygon sees them.
        var pinsPerInstance = new IReadOnlyList<GdsAbsolutePin>[]
        {
            new[] { Pin("out", 5, 5) },
            new[] { Pin("in", 15, 5) },
        };

        var result = Match(new[] { Rect(0, 0, 20, 10) }, pinsPerInstance);

        var pair = result.Pairs.ShouldHaveSingleItem();
        pair.A.PinName.ShouldBe("out");
        pair.B.PinName.ShouldBe("in");
        result.ConsumedPolygonIndexes.ShouldBe(new[] { 0 });
    }
}
