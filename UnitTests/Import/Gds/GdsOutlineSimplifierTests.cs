using CAP_DataAccess.Import.Gds;
using Shouldly;

namespace UnitTests.Import.Gds;

/// <summary>
/// Unit tests for <see cref="GdsOutlineSimplifier"/>: Ramer-Douglas-Peucker
/// simplification, adaptive tolerance growth, the polygon-dropping fallback
/// and the closed-ring contract. Polygons are app-space outline rings
/// (first point repeated at the end), like the importer produces.
/// </summary>
public class GdsOutlineSimplifierTests
{
    /// <summary>Axis-aligned rectangle ring (5 points, first repeated).</summary>
    private static GdsOutlinePolygon Rectangle(int layer, double x, double y, double width, double height) =>
        new()
        {
            Layer = layer,
            DataType = 0,
            Points = new[]
            {
                new GdsOutlinePoint(x, y),
                new GdsOutlinePoint(x + width, y),
                new GdsOutlinePoint(x + width, y + height),
                new GdsOutlinePoint(x, y + height),
                new GdsOutlinePoint(x, y),
            },
        };

    /// <summary>N-gon "circle" ring (n + 1 points, first repeated).</summary>
    private static GdsOutlinePolygon Circle(int layer, double radius, int vertices)
    {
        var points = Enumerable.Range(0, vertices)
            .Select(i =>
            {
                double angle = 2 * Math.PI * i / vertices;
                return new GdsOutlinePoint(radius + radius * Math.Cos(angle), radius + radius * Math.Sin(angle));
            })
            .ToList();
        points.Add(points[0]);
        return new GdsOutlinePolygon { Layer = layer, DataType = 0, Points = points };
    }

    // ── Adaptive tolerance growth ────────────────────────────────────────────

    [Fact]
    public void Simplify_ToleranceGrowsAdaptively_UntilCapIsMet()
    {
        // A 72-gon at a near-zero tolerance survives RDP almost fully; the cap
        // forces the simplifier to grow the tolerance (×4 per round) instead of
        // dropping polygons. Growth to 0.016 µm flattens the 72-gon onto a
        // 32-vertex ring (11.25° spans fall below the grown tolerance).
        var circle = Circle(layer: 3, radius: 5, vertices: 72);

        var result = GdsOutlineSimplifier.Simplify(
            new[] { circle }, toleranceUm: 1e-6, maxTotalPoints: 40, out int dropped);

        dropped.ShouldBe(0, "tolerance growth must satisfy the cap before any polygon is dropped");
        var simplified = result.ShouldHaveSingleItem();
        simplified.Points.Count.ShouldBeLessThanOrEqualTo(40);
        simplified.Points.Count.ShouldBeLessThan(73, "growth beyond the initial tolerance must remove vertices");
        simplified.Layer.ShouldBe(3, "layer/datatype survive simplification");
    }

    [Fact]
    public void Simplify_WithinCapAtInitialTolerance_KeepsEverything()
    {
        var rectangle = Rectangle(layer: 1, 0, 0, 10, 4);

        var result = GdsOutlineSimplifier.Simplify(
            new[] { rectangle }, toleranceUm: 0.05, maxTotalPoints: 2000, out int dropped);

        dropped.ShouldBe(0);
        result.ShouldHaveSingleItem().Points.Count.ShouldBe(5);
    }

    // ── Polygon dropping ─────────────────────────────────────────────────────
    // Dropping only fires after the tolerance has been grown 8× without
    // meeting the cap. The fixtures below use a 1e-9 initial tolerance, so the
    // grown tolerance (≤ 6.5e-5) neither simplifies nor collapses any polygon —
    // the dropping path is exercised deterministically.

    [Fact]
    public void Simplify_CapBelowTotal_DropsSmallestAreaPolygonsFirst()
    {
        // Rectangles do not simplify (every corner is kept), so the only way to
        // satisfy cap 6 with 10 total points is dropping the smaller polygon.
        var big = Rectangle(layer: 1, 0, 0, 10, 10);   // area 100, 5 points
        var small = Rectangle(layer: 2, 0, 0, 1, 1);   // area 1, 5 points

        var result = GdsOutlineSimplifier.Simplify(
            new[] { big, small }, toleranceUm: 1e-9, maxTotalPoints: 6, out int dropped);

        dropped.ShouldBe(1);
        result.ShouldHaveSingleItem().Layer.ShouldBe(1, "the largest-area polygon is kept");
    }

    [Fact]
    public void Simplify_Dropping_KeepsOriginalOrder_NotAreaOrder()
    {
        // Input order: small, BIG, medium — the kept set must come back in the
        // original library order, not sorted by area.
        var small = Rectangle(layer: 1, 0, 0, 1, 1);     // area 1
        var big = Rectangle(layer: 2, 0, 0, 10, 10);     // area 100
        var medium = Rectangle(layer: 3, 0, 0, 5, 5);    // area 25

        var result = GdsOutlineSimplifier.Simplify(
            new[] { small, big, medium }, toleranceUm: 1e-9, maxTotalPoints: 10, out int dropped);

        dropped.ShouldBe(1, "10 points fit big + medium; the smallest is dropped");
        result.Select(p => p.Layer).ShouldBe(new[] { 2, 3 }, "original order is restored after dropping");
    }

    [Fact]
    public void Simplify_CapBelowLargestPolygon_AlwaysKeepsTheLargest()
    {
        // Cap smaller than the largest polygon's own point count: the budget
        // grows to fit the largest polygon — it is never dropped.
        var big = Rectangle(layer: 1, 0, 0, 10, 10);
        var small = Rectangle(layer: 2, 0, 0, 1, 1);

        var result = GdsOutlineSimplifier.Simplify(
            new[] { small, big }, toleranceUm: 1e-9, maxTotalPoints: 1, out int dropped);

        dropped.ShouldBe(1);
        result.ShouldHaveSingleItem().Layer.ShouldBe(1);
    }

    [Fact]
    public void Simplify_EmptyInput_NegativeCap_ReturnsEmpty()
    {
        // A negative cap (invalid options) makes 0 points exceed the cap; the
        // dropping path must not index into an empty list.
        var result = GdsOutlineSimplifier.Simplify(
            Array.Empty<GdsOutlinePolygon>(), toleranceUm: 0.05, maxTotalPoints: -1, out int dropped);

        result.ShouldBeEmpty();
        dropped.ShouldBe(0);
    }

    // ── Closed-ring contract ─────────────────────────────────────────────────

    [Fact]
    public void Simplify_KeepsRingsClosed()
    {
        // RDP keeps the first/last anchor — for a closed ring that is the same
        // point, so every simplified polygon must stay closed.
        var circle = Circle(layer: 1, radius: 5, vertices: 72);

        var result = GdsOutlineSimplifier.Simplify(
            new[] { circle }, toleranceUm: 0.5, maxTotalPoints: 2000, out _);

        var simplified = result.ShouldHaveSingleItem();
        simplified.Points.Count.ShouldBeLessThan(73, "0.5 µm tolerance must simplify a 5 µm-radius 72-gon");
        simplified.Points[^1].ShouldBe(simplified.Points[0], "the ring stays closed after simplification");
    }

    [Fact]
    public void Simplify_PolygonCollapsingBelowTriangle_KeepsOriginalRing()
    {
        // A tiny ring (all vertices within the tolerance of the anchors)
        // collapses below 3 distinct points + closing point. It must NOT be
        // silently removed: it survives with its original, unsimplified points.
        var tiny = Rectangle(layer: 1, 0, 0, 0.001, 0.001);
        var big = Rectangle(layer: 2, 0, 0, 10, 10);

        var result = GdsOutlineSimplifier.Simplify(
            new[] { tiny, big }, toleranceUm: 0.05, maxTotalPoints: 2000, out int dropped);

        dropped.ShouldBe(0, "nothing is dropped — collapse keeps the original ring");
        result.Count.ShouldBe(2);
        result[0].Points.Count.ShouldBe(5, "the collapsed ring falls back to its original geometry");
        result[0].Layer.ShouldBe(1);
    }

    [Fact]
    public void Simplify_EscalationCollapsesSmallPolygons_KeepsLastValidLevel_ThenCountsDrops()
    {
        // 300 small 25-gon circles (radius 0.5 µm) plus one big rectangle. The
        // tolerance escalation (0.05 → ×8) collapses every circle below the ring
        // minimum — each must keep its last valid level (~5 points) instead of
        // vanishing, so the cap is then enforced by COUNTED drops only:
        // survivors + dropped always equals the input count.
        var polygons = Enumerable.Range(0, 300)
            .Select(i => Circle(layer: 2, radius: 0.5, vertices: 25))
            .Append(Rectangle(layer: 1, 0, 0, 100, 100))
            .ToList();

        var result = GdsOutlineSimplifier.Simplify(
            polygons, toleranceUm: 0.05, maxTotalPoints: 1000, out int dropped);

        (result.Count + dropped).ShouldBe(polygons.Count, "no polygon may vanish uncounted");
        dropped.ShouldBeGreaterThan(0, "300 rings at ≥4 points cannot fit a 1000-point cap");
        TotalPoints(result).ShouldBeLessThanOrEqualTo(1000);
        result.ShouldContain(p => p.Layer == 1, "the largest polygon is always kept");
        foreach (var survivor in result)
        {
            survivor.Points.Count.ShouldBeGreaterThanOrEqualTo(4, "every survivor is a valid ring");
            survivor.Points.Count.ShouldBeLessThanOrEqualTo(26, "simplification only removes points");
            survivor.Points[^1].ShouldBe(survivor.Points[0], "every survivor stays a closed ring");
        }
    }

    [Fact]
    public void Simplify_ThousandsOfSmallPolygonsOverCap_NoSilentLoss()
    {
        // The production-file shape that exposed the silent-loss bug: 6364 small
        // residual polygons totaling ~165k points against an 8000-point cap. The
        // old code escalated the tolerance until thousands collapsed below the
        // ring minimum in ONE round and removed them without counting — only 12
        // polygons survived and no warning fired. Now every polygon is either a
        // valid-ring survivor or a counted drop.
        var polygons = Enumerable.Range(0, 6364)
            .Select(_ => Circle(layer: 2, radius: 0.5, vertices: 25))
            .ToList();

        var result = GdsOutlineSimplifier.Simplify(
            polygons, toleranceUm: 0.05, maxTotalPoints: 8000, out int dropped);

        (result.Count + dropped).ShouldBe(6364, "no polygon may vanish uncounted");
        dropped.ShouldBeGreaterThan(0, "6364 rings at ≥4 points each exceed the 8000-point cap");
        result.Count.ShouldBeGreaterThan(1000, "the cap is filled with survivors, not emptied");
        TotalPoints(result).ShouldBeLessThanOrEqualTo(8000);
        result.ShouldAllBe(p => p.Points.Count >= 4, "every survivor is a valid ring");
    }

    private static int TotalPoints(IReadOnlyList<GdsOutlinePolygon> polygons) =>
        polygons.Sum(p => p.Points.Count);

    [Fact]
    public void Simplify_StraightRing_SimplifiesToEndpoints()
    {
        // Collinear intermediate points are within tolerance of the anchor
        // segment and are removed; the area stays ~0 but the ring survives
        // with its anchor points.
        var ring = new GdsOutlinePolygon
        {
            Layer = 1,
            DataType = 0,
            Points = new[]
            {
                new GdsOutlinePoint(0, 0),
                new GdsOutlinePoint(5, 0),
                new GdsOutlinePoint(10, 0),
                new GdsOutlinePoint(10, 1),
                new GdsOutlinePoint(5, 1),
                new GdsOutlinePoint(0, 1),
                new GdsOutlinePoint(0, 0),
            },
        };

        var result = GdsOutlineSimplifier.Simplify(
            new[] { ring }, toleranceUm: 0.05, maxTotalPoints: 2000, out _);

        var simplified = result.ShouldHaveSingleItem();
        simplified.Points.Count.ShouldBe(5, "the two collinear midpoints (5,0) and (5,1) are removed");
        simplified.Points[^1].ShouldBe(simplified.Points[0]);
        simplified.Points.ShouldContain(p => p.X == 10 && p.Y == 0);
    }
}
