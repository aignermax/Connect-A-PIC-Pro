using Shouldly;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Counter-tests for <see cref="ExportedWaveguideOverlapAnalyzer"/>: the honesty
/// proof of #1088 is only as good as its checker, so the checker itself gets
/// pinned on hand-built GDS coordinate JSON — overlapping chains must be blamed
/// by name, disjoint chains must stay clean, footprint-internal contact must be
/// tolerated, dropped routes must report their uncovered pins, and a chain split
/// into disjoint pieces must be caught as broken.
/// </summary>
public class ExportedWaveguideOverlapAnalyzerTests
{
    private const string CellName = "D";

    private static readonly ExportedWaveguideOverlapAnalyzer.BoundingBox[] NoFootprints = [];

    [Fact]
    public void OverlappingChains_NamesTheOffendingPair()
    {
        var connections = new[]
        {
            Connection("A", (0, 0), (10, 0)),
            Connection("B", (5, -3), (5, 3)),
        };
        // A is a horizontal 1 µm strip, B a vertical 1 µm strip cutting through it.
        var json = Gds(
            Polygon(-0.1, -0.5, 10.1, 0.5),
            Polygon(4.45, -3.02, 5.55, 3.02));

        var violations = ExportedWaveguideOverlapAnalyzer.FindViolations(
            json, CellName, connections, NoFootprints);

        violations.Count.ShouldBe(1);
        violations[0].ShouldContain("Connection 'A' overlaps connection 'B'");
        violations[0].ShouldContain("#704");
    }

    [Fact]
    public void DisjointChains_RaisesNoOverlap()
    {
        var connections = new[]
        {
            Connection("A", (0, 0), (10, 0)),
            Connection("B", (12, 0), (22, 0)),
        };
        var json = Gds(
            Polygon(0, -0.5, 10, 0.5),
            Polygon(12, -0.5, 22, 0.5));

        ExportedWaveguideOverlapAnalyzer.FindViolations(json, CellName, connections, NoFootprints)
            .ShouldBeEmpty();
    }

    [Fact]
    public void OverlapInsideComponentFootprint_IsTolerated()
    {
        // Same overlap as the counter-test above, but the whole shared chain sits
        // inside one component footprint — a placed crossing / pin abutment.
        var connections = new[]
        {
            Connection("A", (0, 0), (10, 0)),
            Connection("B", (5, -3), (5, 3)),
        };
        var footprints = new[]
        {
            new ExportedWaveguideOverlapAnalyzer.BoundingBox(-1, 11, -4, 4),
        };
        var json = Gds(
            Polygon(-0.1, -0.5, 10.1, 0.5),
            Polygon(4.45, -3.02, 5.55, 3.02));

        ExportedWaveguideOverlapAnalyzer.FindViolations(json, CellName, connections, footprints)
            .ShouldBeEmpty();
    }

    [Fact]
    public void SharedChainOutsideFootprint_IsReported_EvenWhenAnotherSharedChainIsInside()
    {
        // A and B share two chains at their common start pin: one sits fully
        // inside a component footprint (pin abutment — tolerated), the other
        // reaches outside it. The tolerated chain must not mask the overlap.
        // (The 0.5 µm gap between the shared polygons keeps them disjoint at the
        // 0.02 µm contact tolerance, while the common pin at (0.7, 0) stays within
        // the 1.0 µm coverage tolerance of both.)
        var connections = new[]
        {
            Connection("A", (0.7, 0), (20, 0)),
            Connection("B", (0.7, 0), (30, 0)),
        };
        var footprints = new[]
        {
            new ExportedWaveguideOverlapAnalyzer.BoundingBox(-2, 1.2, -2, 2),
        };
        var json = Gds(
            Polygon(-1, -0.5, 1, 0.5),
            Polygon(1.5, -0.5, 3, 0.5),
            Polygon(19, -0.5, 21, 0.5),
            Polygon(29, -0.5, 31, 0.5));

        var violations = ExportedWaveguideOverlapAnalyzer.FindViolations(
            json, CellName, connections, footprints);

        violations.Where(v => v.Contains("overlaps")).ShouldHaveSingleItem()
            .ShouldContain("Connection 'A' overlaps connection 'B'");
    }

    [Fact]
    public void UncoveredPin_NamesTheDroppedConnection()
    {
        var connections = new[]
        {
            Connection("A", (0, 0), (10, 0)),
            Connection("B", (12, 0), (22, 0)),
        };
        // Only A's geometry made it into the GDS — B was dropped.
        var json = Gds(Polygon(0, -0.5, 10, 0.5));

        var violations = ExportedWaveguideOverlapAnalyzer.FindViolations(
            json, CellName, connections, NoFootprints);

        violations.Count.ShouldBe(1);
        violations[0].ShouldContain("Connection 'B' exported no waveguide geometry");
        violations[0].ShouldContain("dropped");
    }

    [Fact]
    public void BrokenChain_ReportsDisjointGeometry()
    {
        var connections = new[]
        {
            Connection("A", (0, 0), (10, 0)),
        };
        // A's two halves end 0.2 µm apart — too far to touch (tolerance 0.02 µm).
        var json = Gds(
            Polygon(0, -0.5, 4, 0.5),
            Polygon(4.2, -0.5, 10, 0.5));

        var violations = ExportedWaveguideOverlapAnalyzer.FindViolations(
            json, CellName, connections, NoFootprints);

        violations.Count.ShouldBe(1);
        violations[0].ShouldContain("Connection 'A' exported as 2 disjoint geometry chains");
    }

    /// <summary>Builds a <see cref="ExportedWaveguideOverlapAnalyzer.Connection"/> for test use.</summary>
    private static ExportedWaveguideOverlapAnalyzer.Connection Connection(
        string name, (double X, double Y) start, (double X, double Y) end) =>
        new(name,
            new ExportedWaveguideOverlapAnalyzer.Endpoint(name, name + ".start", start.X, start.Y),
            new ExportedWaveguideOverlapAnalyzer.Endpoint(name, name + ".end", end.X, end.Y));

    /// <summary>An axis-aligned rectangle polygon on the waveguide layer, closed.</summary>
    private static string Polygon(double x0, double y0, double x1, double y1) =>
        "{\"layer\":1111,\"datatype\":0,\"vertices\":[" +
        $"[{F(x0)},{F(y0)}],[{F(x1)},{F(y0)}],[{F(x1)},{F(y1)}],[{F(x0)},{F(y1)}],[{F(x0)},{F(y0)}]" +
        "]}";

    /// <summary>The extraction-script JSON document wrapping the given polygons.</summary>
    private static string Gds(params string[] polygons) =>
        "{\"units\":{\"user_unit_m\":1e-6,\"db_unit_m\":1e-9}," +
        "\"cells\":[{\"name\":\"" + CellName + "\",\"paths\":[],\"refs\":[],\"polygons\":[" +
        string.Join(",", polygons) +
        "]}]}";

    /// <summary>Invariant double formatting for hand-built JSON.</summary>
    private static string F(double value) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
