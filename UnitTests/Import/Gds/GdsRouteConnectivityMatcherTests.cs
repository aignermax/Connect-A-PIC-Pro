using CAP_DataAccess.Import.Gds;
using Shouldly;

namespace UnitTests.Import.Gds;

/// <summary>
/// Unit tests for <see cref="GdsRouteConnectivityMatcher"/> with hand-built
/// polygons/pins: the exactly-two-pins rule, the junction note, the
/// port/same-instance exclusions, one-partner-per-pin consumption and the
/// touch test itself (point-in-polygon ∪ outline distance).
///
/// The matcher never looks at layers: restricting the input to waveguide-layer
/// polygons happens upstream (<c>GdsHierarchyImportSession.GetTopCellWaveguidePolygons</c>
/// filters by <c>GdsHierarchyImportOptions.RouteLayers</c>), so every fixture
/// below only feeds polygons that already passed that filter.
/// </summary>
public class GdsRouteConnectivityMatcherTests
{
    private const double Tol = 1.0;

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
        List<string>? infos = null) =>
        GdsRouteConnectivityMatcher.Match(
            polygons,
            pinsPerInstance ?? Array.Empty<IReadOnlyList<GdsAbsolutePin>>(),
            topPortPins ?? Array.Empty<GdsAbsolutePin>(),
            Tol,
            infos ?? new List<string>());

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
        infos.ShouldHaveSingleItem().ShouldContain("junction polygon with 3 pins");
    }

    [Fact]
    public void Match_PolygonBridgingTwoPinsOfSameInstance_NoPairAndPinsStayAvailable()
    {
        // Polygon 0 bridges BOTH pins of instance 0 — a connection needs two
        // partners, so it must not pair AND must not consume the pins: polygon 1
        // (instance0.out ↔ instance1.in) can still claim instance0.out.
        var pinsPerInstance = new IReadOnlyList<GdsAbsolutePin>[]
        {
            new[] { Pin("in", 10, 2), Pin("out", 15, 2) },
            new[] { Pin("in", 20, 2), Pin("out", 30, 2) },
        };
        var polygons = new[] { Rect(10, 1.75, 15, 2.25), Rect(15, 1.75, 20, 2.25) };

        var result = Match(polygons, pinsPerInstance);

        var pair = result.Pairs.ShouldHaveSingleItem();
        pair.A.InstanceIndex.ShouldBe(0);
        pair.A.PinName.ShouldBe("out");
        pair.B.InstanceIndex.ShouldBe(1);
        pair.B.PinName.ShouldBe("in");
        result.ConsumedPolygonIndexes.ToArray().ShouldBe(new[] { 1 }, "the same-instance bridge stays a frozen path");
        result.ConsumedInstancePins.ShouldBe(new[] { (0, 1), (1, 0) }, ignoreOrder: true);
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
        // stay unconsumed, consistent with the same-instance rule.
        var topPortPins = new[] { Pin("o1", 10, 2), Pin("o2", 15, 2) };

        var result = Match(new[] { Bridge() }, topPortPins: topPortPins);

        result.Pairs.ShouldBeEmpty();
        result.ConsumedPolygonIndexes.ShouldBeEmpty();
        result.ConsumedPortIndexes.ShouldBeEmpty();
    }

    [Fact]
    public void Match_PinAlreadyConsumed_IsInvisibleToLaterPolygons()
    {
        // Both polygons touch instance1.in at (15, 2): polygon 0 pairs it with
        // instance0.out, so polygon 1 sees only instance2.in — one visible pin
        // is no pair. First polygon in scan order wins.
        var pinsPerInstance = new IReadOnlyList<GdsAbsolutePin>[]
        {
            new[] { Pin("out", 10, 2) },
            new[] { Pin("in", 15, 2) },
            new[] { Pin("in", 20, 2) },
        };
        var polygons = new[] { Rect(10, 1.75, 15, 2.25), Rect(15, 1.75, 20, 2.25) };

        var result = Match(polygons, pinsPerInstance);

        var pair = result.Pairs.ShouldHaveSingleItem();
        pair.A.PinName.ShouldBe("out");
        pair.B.InstanceIndex.ShouldBe(1);
        pair.B.PinName.ShouldBe("in");
        result.ConsumedPolygonIndexes.ToArray().ShouldBe(new[] { 0 }, "polygon 1 finds no second visible pin");
        result.ConsumedInstancePins.ShouldBe(new[] { (0, 0), (1, 0) }, ignoreOrder: true);
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
