using CAP_DataAccess.Import.Gds;
using Shouldly;

namespace UnitTests.Import.Gds;

/// <summary>
/// Unit tests for <see cref="GdsCellFlattener"/>: reference transforms, array
/// expansion, cycle detection and bounding boxes. Fixtures are in-memory GDS
/// byte streams with 1 db unit = 1 nm (1000 db units = 1 µm).
/// </summary>
public class GdsCellFlattenerTests
{
    private const double Tolerance = 1e-9;

    private static async Task<GdsCellFlattener> ReadFlattener(byte[] gdsBytes)
    {
        using var stream = new MemoryStream(gdsBytes);
        var library = await new GdsReader().ReadAsync(stream);
        return new GdsCellFlattener(library);
    }

    // ── SREF transforms ──────────────────────────────────────────────────────

    [Fact]
    public async Task SRef_90DegreeRotation_RotatesPolygonCounterClockwise()
    {
        // Child: 2×1 µm rectangle. Rotated 90° CCW (Y-up): (x, y) → (−y, x).
        var gds = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("CHILD", 0, 0, angleDegrees: 90.0)
            .EndCell()
            .BeginCell("CHILD")
                .Boundary(1, 0, (0, 0), (2000, 0), (2000, 1000), (0, 1000), (0, 0))
            .EndCell()
            .EndLibrary()
            .ToArray();

        var flattened = await FlattenSinglePolygon(gds);

        flattened.Points[0].X.ShouldBe(0, Tolerance);
        flattened.Points[0].Y.ShouldBe(0, Tolerance);
        flattened.Points[1].X.ShouldBe(0, Tolerance);   // (2, 0) → (0, 2)
        flattened.Points[1].Y.ShouldBe(2, Tolerance);
        flattened.Points[2].X.ShouldBe(-1, Tolerance);  // (2, 1) → (−1, 2)
        flattened.Points[2].Y.ShouldBe(2, Tolerance);
        flattened.Points[3].X.ShouldBe(-1, Tolerance);  // (0, 1) → (−1, 0)
        flattened.Points[3].Y.ShouldBe(0, Tolerance);
    }

    [Fact]
    public async Task SRef_RotationMagnificationAndOffset_Combine()
    {
        // angle 90°, mag 2, offset (10, 20): (x, y) → (−2y, 2x) + (10, 20).
        var gds = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("CHILD", 10000, 20000, angleDegrees: 90.0, magnification: 2.0)
            .EndCell()
            .BeginCell("CHILD")
                .Boundary(1, 0, (1000, 0), (0, 1000), (1000, 0))
            .EndCell()
            .EndLibrary()
            .ToArray();

        var flattened = await FlattenSinglePolygon(gds);

        flattened.Points[0].X.ShouldBe(10, Tolerance);  // (1, 0) → (10, 22)
        flattened.Points[0].Y.ShouldBe(22, Tolerance);
        flattened.Points[1].X.ShouldBe(8, Tolerance);   // (0, 1) → (8, 20)
        flattened.Points[1].Y.ShouldBe(20, Tolerance);
    }

    [Fact]
    public async Task SRef_Reflection_MirrorsAboutXAxis()
    {
        // Reflection about X (STRANS bit 15): (x, y) → (x, −y). Angle 0 → exact.
        var gds = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("CHILD", 0, 0, reflected: true)
            .EndCell()
            .BeginCell("CHILD")
                .Boundary(1, 0, (1000, 2000), (3000, 2000), (1000, 2000))
            .EndCell()
            .EndLibrary()
            .ToArray();

        var flattened = await FlattenSinglePolygon(gds);

        flattened.Points[0].ShouldBe(new GdsPoint(1, -2));
        flattened.Points[1].ShouldBe(new GdsPoint(3, -2));
    }

    [Fact]
    public async Task SRef_ReflectionAppliesBeforeRotation()
    {
        // Reflected, then rotated 90° CCW: (x, y) → (x, −y) → (y, x).
        var gds = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("CHILD", 0, 0, angleDegrees: 90.0, reflected: true)
            .EndCell()
            .BeginCell("CHILD")
                .Boundary(1, 0, (1000, 2000), (1000, 1000), (1000, 2000))
            .EndCell()
            .EndLibrary()
            .ToArray();

        var flattened = await FlattenSinglePolygon(gds);

        flattened.Points[0].X.ShouldBe(2, Tolerance);   // (1, 2) → (1, −2) → (2, 1)
        flattened.Points[0].Y.ShouldBe(1, Tolerance);
        flattened.Points[1].X.ShouldBe(1, Tolerance);   // (1, 1) → (1, −1) → (1, 1)
        flattened.Points[1].Y.ShouldBe(1, Tolerance);
    }

    [Fact]
    public async Task NestedReferences_AccumulateTransforms()
    {
        var gds = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("MID", 10000, 0)
            .EndCell()
            .BeginCell("MID")
                .SRef("LEAF", 0, 5000)
            .EndCell()
            .BeginCell("LEAF")
                .Boundary(1, 0, (1000, 1000), (2000, 1000), (1000, 1000))
            .EndCell()
            .EndLibrary()
            .ToArray();

        var flattened = await FlattenSinglePolygon(gds);

        flattened.Points[0].ShouldBe(new GdsPoint(11, 6));
        flattened.Points[1].ShouldBe(new GdsPoint(12, 6));
    }

    // ── AREF expansion ───────────────────────────────────────────────────────

    [Fact]
    public async Task ARef_ExpandsAllInstancesInFlattenAndInstanceTree()
    {
        var gds = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .ARef("CHILD", columns: 3, rows: 2, originX: 1000, originY: 1000,
                    columnSpacingDbUnits: 4000, rowSpacingDbUnits: 3000)
            .EndCell()
            .BeginCell("CHILD")
                .Boundary(1, 0, (0, 0), (500, 0), (0, 0))
            .EndCell()
            .EndLibrary()
            .ToArray();

        var flattener = await ReadFlattener(gds);

        flattener.Flatten("TOP").Polygons.Count.ShouldBe(6);

        var instances = flattener.GetInstanceTree("TOP");
        instances.Count.ShouldBe(6);
        instances.ShouldAllBe(i => i.CellName == "CHILD");
        // Angle 0 → lattice offsets are exact: origin (1,1) + (4c, 3r).
        instances[0].Offset.ShouldBe(new GdsPoint(1, 1));
        instances[2].Offset.ShouldBe(new GdsPoint(9, 1));
        instances[3].Offset.ShouldBe(new GdsPoint(1, 4));
        instances[5].Offset.ShouldBe(new GdsPoint(9, 4));
    }

    [Fact]
    public async Task ARef_Rotated90_RotatesTheLattice()
    {
        var gds = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .ARef("CHILD", columns: 2, rows: 1, originX: 1000, originY: 1000,
                    columnSpacingDbUnits: 4000, rowSpacingDbUnits: 3000, angleDegrees: 90.0)
            .EndCell()
            .BeginCell("CHILD")
                .Boundary(1, 0, (0, 0), (500, 0), (0, 0))
            .EndCell()
            .EndLibrary()
            .ToArray();

        var flattener = await ReadFlattener(gds);
        var instances = flattener.GetInstanceTree("TOP");

        instances.Count.ShouldBe(2);
        instances[1].Offset.X.ShouldBe(1, Tolerance);  // (1,1) + R90·(4,0) = (1, 5)
        instances[1].Offset.Y.ShouldBe(5, Tolerance);
    }

    // ── Instance tree ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetInstanceTree_ReturnsDirectChildrenOnly()
    {
        var gds = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("MID", 2000, 3000, angleDegrees: 45.0, magnification: 1.5)
            .EndCell()
            .BeginCell("MID")
                .SRef("LEAF", 0, 0)
            .EndCell()
            .BeginCell("LEAF")
                .Boundary(1, 0, (0, 0), (1000, 0), (0, 0))
            .EndCell()
            .EndLibrary()
            .ToArray();

        var flattener = await ReadFlattener(gds);
        var instances = flattener.GetInstanceTree("TOP");

        var instance = instances.ShouldHaveSingleItem();
        instance.CellName.ShouldBe("MID"); // not LEAF — hierarchy stays intact
        instance.Offset.ShouldBe(new GdsPoint(2, 3));
        instance.AngleDegrees.ShouldBe(45.0);
        instance.Magnification.ShouldBe(1.5);
        instance.Reflected.ShouldBeFalse();
    }

    // ── Cycle detection ──────────────────────────────────────────────────────

    [Fact]
    public async Task CyclicReferences_ReadFineButFlattenThrows()
    {
        var gds = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("A")
                .SRef("B", 0, 0)
            .EndCell()
            .BeginCell("B")
                .SRef("A", 0, 0)
            .EndCell()
            .EndLibrary()
            .ToArray();

        // Reading is order-tolerant and does not resolve references — must succeed.
        using var stream = new MemoryStream(gds);
        var library = await new GdsReader().ReadAsync(stream);
        library.Cells.Count.ShouldBe(2);

        var flattener = new GdsCellFlattener(library);
        var ex = Should.Throw<InvalidDataException>(() => flattener.Flatten("A"));
        ex.Message.ShouldContain("Cyclic");
        ex.Message.ShouldContain("A");
        ex.Message.ShouldContain("B");
        // Same guard on the bounding-box walk — must not hang either.
        Should.Throw<InvalidDataException>(() => flattener.GetBoundingBox("A"));
    }

    [Fact]
    public async Task SelfReference_Throws()
    {
        var gds = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("A")
                .SRef("A", 0, 0)
            .EndCell()
            .EndLibrary()
            .ToArray();

        var flattener = await ReadFlattener(gds);

        var ex = Should.Throw<InvalidDataException>(() => flattener.Flatten("A"));
        ex.Message.ShouldContain("Cyclic");
    }

    [Fact]
    public async Task UnknownReferencedCell_ThrowsAtFlattenTimeNotReadTime()
    {
        var gds = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("MISSING", 0, 0)
            .EndCell()
            .EndLibrary()
            .ToArray();

        var flattener = await ReadFlattener(gds); // read must succeed

        var ex = Should.Throw<InvalidDataException>(() => flattener.Flatten("TOP"));
        ex.Message.ShouldContain("MISSING");
    }

    // ── Bounding boxes ───────────────────────────────────────────────────────

    [Fact]
    public async Task BoundingBox_SpansOwnAndReferencedGeometry()
    {
        var gds = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .Boundary(1, 0, (-1000, -1000), (0, 0), (-1000, -1000))
                .SRef("CHILD", 10000, 10000)
            .EndCell()
            .BeginCell("CHILD")
                .Boundary(1, 0, (0, 0), (2000, 3000), (0, 0))
            .EndCell()
            .EndLibrary()
            .ToArray();

        var flattener = await ReadFlattener(gds);
        var box = flattener.GetBoundingBox("TOP");

        box.MinX.ShouldBe(-1);
        box.MinY.ShouldBe(-1);
        box.MaxX.ShouldBe(12);
        box.MaxY.ShouldBe(13);
    }

    [Fact]
    public async Task BoundingBox_EmptyCell_IsZero()
    {
        var gds = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP").EndCell()
            .EndLibrary()
            .ToArray();

        var flattener = await ReadFlattener(gds);

        flattener.GetBoundingBox("TOP").ShouldBe(GdsBoundingBox.Empty);
    }

    [Fact]
    public async Task BoundingBox_IncludesPathWidth()
    {
        var gds = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .Path(1, 0, widthDbUnits: 1000, pathType: 0, (0, 0), (4000, 0))
            .EndCell()
            .EndLibrary()
            .ToArray();

        var flattener = await ReadFlattener(gds);
        var box = flattener.GetBoundingBox("TOP");

        box.MinX.ShouldBe(-0.5);
        box.MaxX.ShouldBe(4.5);
        box.MinY.ShouldBe(-0.5);
        box.MaxY.ShouldBe(0.5);
    }

    // ── Texts ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TextAngle_AccumulatesThroughReferences()
    {
        var gds = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("CHILD", 0, 0, angleDegrees: 45.0)
            .EndCell()
            .BeginCell("CHILD")
                .Text(1, 0, "LBL", 1000, 2000, angleDegrees: 30.0)
            .EndCell()
            .EndLibrary()
            .ToArray();

        var flattener = await ReadFlattener(gds);
        var text = flattener.Flatten("TOP").Texts.ShouldHaveSingleItem();

        text.AngleDegrees.ShouldBe(75.0, Tolerance);
    }

    [Fact]
    public async Task TextAngle_FlipsSignUnderReflection()
    {
        var gds = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("CHILD", 0, 0, angleDegrees: 45.0, reflected: true)
            .EndCell()
            .BeginCell("CHILD")
                .Text(1, 0, "LBL", 0, 0, angleDegrees: 30.0)
            .EndCell()
            .EndLibrary()
            .ToArray();

        var flattener = await ReadFlattener(gds);
        var text = flattener.Flatten("TOP").Texts.ShouldHaveSingleItem();

        text.AngleDegrees.ShouldBe(15.0, Tolerance); // 45 − 30
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<GdsPolygon> FlattenSinglePolygon(byte[] gds)
    {
        var flattener = await ReadFlattener(gds);
        return flattener.Flatten("TOP").Polygons.ShouldHaveSingleItem();
    }
}
