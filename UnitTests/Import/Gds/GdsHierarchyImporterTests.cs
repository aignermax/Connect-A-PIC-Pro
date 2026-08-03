using CAP_DataAccess.Import.Gds;
using Shouldly;

namespace UnitTests.Import.Gds;

/// <summary>
/// Tests for <see cref="GdsHierarchyImporter"/>. Fixtures are built with
/// <see cref="GdsTestWriter"/> (1 db unit = 1 nm, so µm values appear ×1000)
/// and read through <see cref="GdsReader"/> — the same path real files take.
/// Expected coordinates below are in micrometers, app space (Y-down, origin at
/// the top-cell bbox top-left).
/// </summary>
public class GdsHierarchyImporterTests
{
    private const double Tolerance = 1e-6;

    // ── Explode: abutment end-to-end ─────────────────────────────────────────

    [Fact]
    public async Task Explode_TwoAbuttingWaveguides_YieldsDraftsInstancesAndOneConnection()
    {
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("wgA", 0, 0)
                .SRef("wgB", 10000, 0)
            .EndCell()
            .WaveguideCell("wgA")
            .WaveguideCell("wgB")
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        result.Mode.ShouldBe(GdsHierarchyImportMode.ExplodeHierarchy);
        result.TopCellName.ShouldBe("TOP");
        result.BoundingBox.MaxX.ShouldBe(20, Tolerance);
        result.BoundingBox.MaxY.ShouldBe(4, Tolerance);
        result.Warnings.ShouldBeEmpty();

        // Two drafts, in order of first appearance.
        result.ImportedCellDrafts.Count.ShouldBe(2);
        var wgA = result.ImportedCellDrafts[0];
        wgA.CellName.ShouldBe("wgA");
        wgA.WidthUm.ShouldBe(10, Tolerance);
        wgA.HeightUm.ShouldBe(4, Tolerance);
        wgA.Pins.Count.ShouldBe(2);
        wgA.Pins[0].Name.ShouldBe("in"); // left edge sorts first
        wgA.Pins[0].XUm.ShouldBe(0, Tolerance);
        wgA.Pins[0].YUm.ShouldBe(2, Tolerance);
        wgA.Pins[0].AngleDegrees.ShouldBe(180, Tolerance);
        wgA.Pins[1].Name.ShouldBe("out");
        wgA.Pins[1].XUm.ShouldBe(10, Tolerance);
        wgA.Pins[1].AngleDegrees.ShouldBe(0, Tolerance);
        AssertPinsWithinDraftBounds(wgA);
        AssertPinsWithinDraftBounds(result.ImportedCellDrafts[1]);

        // RawCode round-trip snippet with the file-name token: the loaded cell is
        // re-anchored to its bbox bottom-left (the app-space top-left origin), and
        // topcellsonly=False because the imported cell is a SUBcell of TOP.
        wgA.RawCodeBackend.ShouldBe("nazca");
        wgA.RawCode.ShouldContain("def component():");
        wgA.RawCode.ShouldContain("nd.load_gds(filename=\"{GdsFileName}\", cellname=\"wgA\", topcellsonly=False)");
        wgA.RawCode.ShouldContain("_loaded.put(-_bb[0], -_bb[1])");

        // Two instances at app-space positions.
        result.Instances.Count.ShouldBe(2);
        result.Instances[0].InstanceName.ShouldBe("wgA#0");
        result.Instances[0].CellDraftName.ShouldBe("wgA");
        result.Instances[0].KnownComponentIdentifier.ShouldBeNull();
        result.Instances[0].PositionXUm.ShouldBe(0, Tolerance);
        result.Instances[0].PositionYUm.ShouldBe(0, Tolerance);
        result.Instances[0].RotationDegrees.ShouldBe(0, Tolerance);
        result.Instances[0].Reflected.ShouldBeFalse();
        result.Instances[1].PositionXUm.ShouldBe(10, Tolerance);
        result.Instances[1].PositionYUm.ShouldBe(0, Tolerance);

        // Exactly one connection: wgA.out ↔ wgB.in at (10, 2).
        var connection = result.Connections.ShouldHaveSingleItem();
        connection.A.InstanceIndex.ShouldBe(0);
        connection.A.PinName.ShouldBe("out");
        connection.B.InstanceIndex.ShouldBe(1);
        connection.B.PinName.ShouldBe("in");
        connection.XUm.ShouldBe(10, Tolerance);
        connection.YUm.ShouldBe(2, Tolerance);
    }

    // ── Known-component resolution ───────────────────────────────────────────

    [Fact]
    public async Task Explode_HashSuffixedCell_ResolvesToBaseKnownComponent()
    {
        // A gdsfactory-style hashed cell name resolves to the base PDK name;
        // its pins come from the resolver (authoritative PDK pin names).
        var known = new KnownComponent(
            "mmi1x2", "testpdk", 30, 10,
            new[]
            {
                Pin("o1", 0, 5, 180),
                Pin("o2", 30, 5, 0),
            });

        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("mmi1x2_A1B2C3", 0, 0)
                .SRef("wgB", 30000, 3000)
            .EndCell()
            .BeginCell("mmi1x2_A1B2C3")
                .Boundary(1, 0, (0, 0), (30000, 0), (30000, 10000), (0, 10000), (0, 0))
            .EndCell()
            .WaveguideCell("wgB")
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(
            library, "TOP", new GdsHierarchyImportOptions
            {
                ResolveKnownComponent = name => name == "mmi1x2" ? known : null,
            });

        // The known cell produces no draft; only the unknown wgB does.
        result.ImportedCellDrafts.ShouldHaveSingleItem().CellName.ShouldBe("wgB");

        result.Instances.Count.ShouldBe(2);
        result.Instances[0].KnownComponentIdentifier.ShouldBe("mmi1x2");
        result.Instances[0].PdkSource.ShouldBe("testpdk");
        result.Instances[0].CellDraftName.ShouldBeNull();
        result.Instances[1].CellDraftName.ShouldBe("wgB");

        // mmi.o2 at GDS (30, 5) abuts wgB.in (offset (30, 3) + cell-local (0, 2)).
        var connection = result.Connections.ShouldHaveSingleItem();
        connection.A.InstanceIndex.ShouldBe(0);
        connection.A.PinName.ShouldBe("o2");
        connection.B.InstanceIndex.ShouldBe(1);
        connection.B.PinName.ShouldBe("in");
        connection.XUm.ShouldBe(30, Tolerance);
        connection.YUm.ShouldBe(5, Tolerance);
        // Resolution visibility: the user sees which library component the cell
        // was bound to — as an INFO note, not a warning (a successful binding
        // is the normal case).
        result.Warnings.ShouldBeEmpty();
        result.Infos.ShouldHaveSingleItem().ShouldContain("resolved to existing component 'mmi1x2'");
    }

    [Fact]
    public async Task Explode_ExactCellNameMatch_ResolvesWithoutStripping()
    {
        var known = new KnownComponent(
            "wgA", "testpdk", 10, 4,
            new[]
            {
                Pin("in", 0, 2, 180),
                Pin("out", 10, 2, 0),
            });

        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("wgA", 0, 0)
                .SRef("wgB", 10000, 0)
            .EndCell()
            .WaveguideCell("wgA")
            .WaveguideCell("wgB")
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(
            library, "TOP", new GdsHierarchyImportOptions
            {
                ResolveKnownComponent = name => name == "wgA" ? known : null,
            });

        result.ImportedCellDrafts.ShouldHaveSingleItem().CellName.ShouldBe("wgB");
        result.Instances[0].KnownComponentIdentifier.ShouldBe("wgA");
        // Connection reconstruction uses the resolver-supplied pins.
        result.Connections.ShouldHaveSingleItem().A.PinName.ShouldBe("out");
        // Resolution visibility: the binding lands in the info-notes channel.
        result.Warnings.ShouldBeEmpty();
        result.Infos.ShouldHaveSingleItem().ShouldContain("resolved to existing component 'wgA'");
    }

    [Fact]
    public async Task Explode_AmbiguousStrippedNames_NeverGuessed_BecomesDraftWithWarning()
    {
        // Both "thing_AB12" and "thing" resolve to DIFFERENT components: the
        // importer must not guess — the cell becomes a draft.
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("thing_AB12_CD34", 0, 0)
            .EndCell()
            .WaveguideCell("thing_AB12_CD34")
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(
            library, "TOP", new GdsHierarchyImportOptions
            {
                ResolveKnownComponent = name => name switch
                {
                    "thing_AB12" => new KnownComponent("thingAB", "pdk", 10, 4, Array.Empty<DetectedPin>()),
                    "thing" => new KnownComponent("thingBase", "pdk", 10, 4, Array.Empty<DetectedPin>()),
                    _ => null,
                },
            });

        result.ImportedCellDrafts.ShouldHaveSingleItem().CellName.ShouldBe("thing_AB12_CD34");
        result.Instances[0].KnownComponentIdentifier.ShouldBeNull();
        result.Instances[0].CellDraftName.ShouldBe("thing_AB12_CD34");
        result.Warnings.ShouldContain(w => w.Contains("ambiguous") && w.Contains("thing_AB12_CD34"));
    }

    // ── Transforms ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Explode_RotatedInstance90_PinsProjectedNumericallyCorrect()
    {
        // B is rotated 90° CCW (GDS, Y-up) at offset (10, 6) µm. Worked example
        // (all µm): cell "wg" 10×4, pins in=(0,2,180°), out=(10,2,0°).
        // T: x′ = −y + 10, y′ = x + 6. Top bbox = (0,0)-(10,16).
        //   B.in : (0,2) → GDS (8,6)  → app (8, 10), west → down  (90°)
        //   B.out: (10,2) → GDS (8,16) → app (8, 0),  east → up   (270°)
        //   B placed bbox top-left: (6, 0); app rotation = −90° ≡ 270°.
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("wg", 0, 0)
                .SRef("wg", 10000, 6000, angleDegrees: 90)
            .EndCell()
            .WaveguideCell("wg")
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        result.ImportedCellDrafts.ShouldHaveSingleItem().CellName.ShouldBe("wg");
        result.Instances.Count.ShouldBe(2);
        result.Instances[0].PositionXUm.ShouldBe(0, Tolerance);
        result.Instances[0].PositionYUm.ShouldBe(12, Tolerance); // 16 − 4: A sits at the bottom
        result.Instances[1].PositionXUm.ShouldBe(6, Tolerance);
        result.Instances[1].PositionYUm.ShouldBe(0, Tolerance);
        result.Instances[1].RotationDegrees.ShouldBe(270, Tolerance); // GDS +90° ≡ app −90°
        result.Connections.ShouldBeEmpty();

        // Numeric projection check of the rotated pins through the internal projector.
        var flattener = new GdsCellFlattener(library);
        var gdsInstances = flattener.GetInstanceTree("TOP");
        var cellBBox = flattener.GetBoundingBox("wg");
        var topBBox = flattener.GetBoundingBox("TOP");
        var pins = new[] { Pin("in", 0, 2, 180), Pin("out", 10, 2, 0) };

        var projected = GdsInstancePinProjector.ProjectPins(gdsInstances[1], cellBBox, pins, topBBox);
        projected[0].Name.ShouldBe("in");
        projected[0].XUm.ShouldBe(8, Tolerance);
        projected[0].YUm.ShouldBe(10, Tolerance);
        projected[0].AngleDegrees.ShouldBe(90, Tolerance);
        projected[1].Name.ShouldBe("out");
        projected[1].XUm.ShouldBe(8, Tolerance);
        projected[1].YUm.ShouldBe(0, Tolerance);
        projected[1].AngleDegrees.ShouldBe(270, Tolerance);
    }

    [Fact]
    public async Task Explode_ReflectedInstance_WarnsAndReconstructionUsesReflectedTransform()
    {
        // Asymmetric pin: "in" at cell GDS (0, 3). Mirrored about the cell's X
        // axis it lands at GDS (0, −3); the top bbox becomes (0,−4)-(10,0), so
        // app Y = 0 − (−3) = 3 — an unreflected reconstruction would say 1.
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("wg", 0, 0, reflected: true)
            .EndCell()
            .WaveguideCell("wg", inY: 3000)
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        var instance = result.Instances.ShouldHaveSingleItem();
        instance.Reflected.ShouldBeTrue();
        result.Warnings.ShouldContain(w => w.Contains("mirrored") && w.Contains("unreflected"));
        result.Infos.ShouldBeEmpty("transform caveats stay warnings — nothing informational here");

        var flattener = new GdsCellFlattener(library);
        var projected = GdsInstancePinProjector.ProjectPins(
            flattener.GetInstanceTree("TOP")[0],
            flattener.GetBoundingBox("wg"),
            new[] { Pin("in", 0, 1, 180), Pin("out", 10, 2, 0) },
            flattener.GetBoundingBox("TOP"));

        // Note: the cell-local app pin (0,1) — app frame of the UNREFLECTED
        // cell — is what gets projected through the true reflected transform.
        projected[0].XUm.ShouldBe(0, Tolerance);
        projected[0].YUm.ShouldBe(3, Tolerance);
        projected[0].AngleDegrees.ShouldBe(180, Tolerance); // X-mirror keeps horizontal directions
        projected[1].XUm.ShouldBe(10, Tolerance);
        projected[1].YUm.ShouldBe(2, Tolerance);
    }

    [Fact]
    public async Task Explode_NonCardinalAngle_SnappedToNearestCardinalWithWarning()
    {
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("wg", 0, 0, angleDegrees: 45)
            .EndCell()
            .WaveguideCell("wg")
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        result.Instances.ShouldHaveSingleItem().RotationDegrees.ShouldBe(270, Tolerance); // 45° → 90° → app −90°
        result.Warnings.ShouldContain(w =>
            w.Contains("45") && w.Contains("90") && w.Contains("Manhattan"));
        result.Infos.ShouldBeEmpty("the rotation snap is a caveat, not an informational note");
    }

    // ── Abutment edge cases ──────────────────────────────────────────────────

    [Fact]
    public async Task Explode_AmbiguousPinPartners_WarnsAndFirstMatchWins()
    {
        // src.out coincides with BOTH sink instances' "in" pins (30 nm apart,
        // within the 0.05 µm default tolerance).
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("src", 0, 0)
                .SRef("sink", 10000, 0)
                .SRef("sink", 10000, 30)
            .EndCell()
            .WaveguideCell("src")
            .WaveguideCell("sink")
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        var connection = result.Connections.ShouldHaveSingleItem();
        connection.A.InstanceIndex.ShouldBe(0);
        connection.A.PinName.ShouldBe("out");
        connection.B.InstanceIndex.ShouldBe(1); // first sink in placement order wins
        connection.B.PinName.ShouldBe("in");
        result.Warnings.ShouldContain(w => w.Contains("candidates") && w.Contains("src#0"));
    }

    [Fact]
    public async Task Explode_TopLevelLabels_BecomeExternalPortConnections()
    {
        // Circuit ports as top-level labels; the label at the internal abutment
        // must NOT steal the instance-to-instance connection (instances win).
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("wgA", 0, 0)
                .SRef("wgB", 10000, 0)
                .Text(1, 10, "in0", 0, 2000)
                .Text(1, 10, "mid", 10000, 2000)
                .Text(1, 10, "out0", 20000, 2000)
            .EndCell()
            .WaveguideCell("wgA")
            .WaveguideCell("wgB")
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        result.Connections.Count.ShouldBe(3);

        var abutment = result.Connections.Where(c => c.A.PinName == "out" && c.B.PinName == "in").ShouldHaveSingleItem();
        abutment.A.InstanceIndex.ShouldBe(0);
        abutment.B.InstanceIndex.ShouldBe(1);

        var input = result.Connections.Where(c => c.B.PinName == "in0").ShouldHaveSingleItem();
        input.A.InstanceIndex.ShouldBe(0);
        input.A.PinName.ShouldBe("in");
        input.B.IsTopLevelPort.ShouldBeTrue();
        input.XUm.ShouldBe(0, Tolerance);
        input.YUm.ShouldBe(2, Tolerance);

        var output = result.Connections.Where(c => c.B.PinName == "out0").ShouldHaveSingleItem();
        output.A.InstanceIndex.ShouldBe(1);
        output.A.PinName.ShouldBe("out");
        output.B.IsTopLevelPort.ShouldBeTrue();

        // The "mid" port lost to the instance-to-instance pair (one partner per pin).
        result.Connections.ShouldNotContain(c => c.B.PinName == "mid");
    }

    // ── Black-box mode ───────────────────────────────────────────────────────

    [Fact]
    public async Task BlackBox_TopCell_BecomesSingleDraftWithoutInstances()
    {
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("wgA", 0, 0)
                .SRef("wgB", 10000, 0)
                .Text(1, 10, "a0", 0, 2000)
                .Text(1, 10, "a1", 20000, 2000)
            .EndCell()
            .WaveguideCell("wgA")
            .WaveguideCell("wgB")
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(
            library, "TOP", new GdsHierarchyImportOptions { Mode = GdsHierarchyImportMode.BlackBox });

        result.Mode.ShouldBe(GdsHierarchyImportMode.BlackBox);
        result.Instances.ShouldBeEmpty();
        result.Connections.ShouldBeEmpty();

        var draft = result.ImportedCellDrafts.ShouldHaveSingleItem();
        draft.CellName.ShouldBe("TOP");
        draft.WidthUm.ShouldBe(20, Tolerance);
        draft.HeightUm.ShouldBe(4, Tolerance);

        // Only the top cell's OWN labels are ports — child labels stay internal.
        draft.Pins.Count.ShouldBe(2);
        draft.Pins[0].Name.ShouldBe("a0");
        draft.Pins[1].Name.ShouldBe("a1");
        AssertPinsWithinDraftBounds(draft);

        // Outlines absorb the whole hierarchy (stripe + extent per child).
        draft.Outlines.Count.ShouldBe(4);
        result.Warnings.ShouldBeEmpty();
    }

    // ── Outlines ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task BlackBox_Outline_SimplifiedUnderPointCap_LayerKept_YDown()
    {
        // 72-gon "circle" (radius 5 µm) on layer 3 plus a bbox rectangle on
        // layer 1; point cap 25 forces adaptive tolerance growth.
        var circlePoints = Enumerable.Range(0, 72)
            .Select(i =>
            {
                double angle = 2 * Math.PI * i / 72;
                return ((int)Math.Round(5000 + 5000 * Math.Cos(angle)),
                        (int)Math.Round(5000 + 5000 * Math.Sin(angle)));
            })
            .Append((10000, 5000)) // close the ring
            .ToArray();

        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .Boundary(3, 0, circlePoints)
                .Boundary(1, 0, (0, 0), (10000, 0), (10000, 10000), (0, 10000), (0, 0))
            .EndCell()
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(
            library, "TOP", new GdsHierarchyImportOptions
            {
                Mode = GdsHierarchyImportMode.BlackBox,
                MaxOutlinePointsPerCell = 25,
            });

        var draft = result.ImportedCellDrafts.ShouldHaveSingleItem();
        draft.Outlines.Count.ShouldBe(2);
        draft.Outlines.Sum(p => p.Points.Count).ShouldBeLessThanOrEqualTo(25);

        // Layer/datatype survive simplification.
        draft.Outlines.ShouldContain(p => p.Layer == 3 && p.DataType == 0);
        draft.Outlines.ShouldContain(p => p.Layer == 1 && p.DataType == 0);

        // App-space Y-down: the rectangle's top edge (GDS MaxY) maps to y = 0.
        var rectangle = draft.Outlines.First(p => p.Layer == 1);
        rectangle.Points.ShouldContain(pt => pt.Y == 0 && pt.X == 0);
        rectangle.Points.ShouldContain(pt => pt.Y == 0 && pt.X == 10);
        rectangle.Points.Min(pt => pt.Y).ShouldBe(0, Tolerance);
        rectangle.Points.Max(pt => pt.Y).ShouldBe(10, Tolerance);
    }

    // ── Validation ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_UnknownTopCell_ThrowsInvalidData()
    {
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP").EndCell()
            .EndLibrary()
            .ToArray());

        await Should.ThrowAsync<InvalidDataException>(
            () => GdsHierarchyImporter.ImportAsync(library, "MISSING", new GdsHierarchyImportOptions()));
    }

    [Fact]
    public async Task ImportAsync_EmptyLibrary_ThrowsInvalidData()
    {
        await Should.ThrowAsync<InvalidDataException>(
            () => GdsHierarchyImporter.ImportAsync(new GdsLibrary(), "TOP", new GdsHierarchyImportOptions()));
    }

    [Fact]
    public async Task Explode_TopCellWithoutInstances_WarnsNothingToExplode()
    {
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .Boundary(1, 0, (0, 0), (10000, 0), (10000, 4000), (0, 4000), (0, 0))
            .EndCell()
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        result.ImportedCellDrafts.ShouldBeEmpty();
        result.Instances.ShouldBeEmpty();
        result.Connections.ShouldBeEmpty();
        result.Warnings.ShouldContain(w => w.Contains("nothing to explode"));
        result.Warnings.ShouldContain(w => w.Contains("own") && w.Contains("not reconstructed"));
    }

    // ── Zero-geometry / export-artifact cells ────────────────────────────────

    [Fact]
    public async Task Explode_ZeroGeometryCell_DroppedWithOneInfoNote_InstancesExcluded()
    {
        // "zeroL" mimics a gdsfactory zero-length straight (a route_bundle
        // artifact): its only content is a port label at a single point, so the
        // flattened bbox is empty (0 × 0). Two instances of it sit exactly on
        // wgA's pins — the wgA.out ↔ wgB.in abutment must survive untouched.
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("wgA", 0, 0)
                .SRef("zeroL", 0, 2000)
                .SRef("zeroL", 10000, 2000)
                .SRef("wgB", 10000, 0)
            .EndCell()
            .WaveguideCell("wgA")
            .BeginCell("zeroL")
                .Text(1, 10, "io", 0, 0)
            .EndCell()
            .WaveguideCell("wgB")
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        // The normal cells import as before; the zero-geometry cell leaves no
        // draft and no instances behind.
        result.ImportedCellDrafts.Select(d => d.CellName).ShouldBe(new[] { "wgA", "wgB" });
        result.Instances.Count.ShouldBe(2);
        result.Instances.ShouldNotContain(i => i.CellName == "zeroL");

        // The old cascade (empty-bbox warning, "not registered: zero size",
        // per-instance placement skips) collapses into ONE info note per cell.
        result.Warnings.ShouldBeEmpty();
        var note = result.Infos.ShouldHaveSingleItem();
        note.ShouldContain("'zeroL'");
        note.ShouldContain("2 instance(s) skipped");

        // The surviving connection is the wgA.out ↔ wgB.in abutment — nothing
        // references the dropped instances (they never entered the matcher).
        var connection = result.Connections.ShouldHaveSingleItem();
        connection.A.PinName.ShouldBe("out");
        connection.B.PinName.ShouldBe("in");
        connection.XUm.ShouldBe(10, Tolerance);
        connection.YUm.ShouldBe(2, Tolerance);
    }

    [Fact]
    public async Task Explode_LunimaExportArtifactCell_SkippedWithOneInfoNote()
    {
        // 'ConnectAPIC_NazcaPartial' is the top cell name our mixed-backend
        // export (MixedBackendGdsOrchestrator.NazcaPartialTopCellName) writes
        // for the flattened nazca partial. It HAS geometry (on a non-port
        // layer, hence pinless) — the skip is by name convention, independent
        // of the zero-geometry drop. Two references still yield ONE note.
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("wgA", 0, 0)
                .SRef("ConnectAPIC_NazcaPartial", 20000, 0)
                .SRef("ConnectAPIC_NazcaPartial", 40000, 0)
            .EndCell()
            .WaveguideCell("wgA")
            .BeginCell("ConnectAPIC_NazcaPartial")
                .Boundary(111, 0, (0, 0), (5000, 0), (5000, 3000), (0, 3000), (0, 0))
            .EndCell()
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        result.ImportedCellDrafts.ShouldHaveSingleItem().CellName.ShouldBe("wgA");
        result.Instances.ShouldHaveSingleItem().CellName.ShouldBe("wgA");
        result.Warnings.ShouldBeEmpty(
            "the artifact is skipped by convention, not failed as an unpersistable draft");
        var note = result.Infos.ShouldHaveSingleItem();
        note.ShouldContain("ConnectAPIC_NazcaPartial");
        note.ShouldContain("export artifact");
        note.ShouldContain("not reconstructed");
    }

    [Fact]
    public async Task Explode_LunimaExportArtifactBehindPassThroughWrapper_SkippedWithOneInfoNote()
    {
        // The user's mixed-backend round-trip: gdsfactory's import_gds names the
        // merged component after the source file's TOP cell, so the flattened
        // nazca partial re-enters the merged file behind nazca's default 'nazca'
        // pass-through wrapper (one untransformed reference, nothing else). A
        // name-only artifact check misses this — the wrapper became a pin-less
        // draft and failed with "not registered: no pins" plus a skipped
        // placement, the exact cascade the artifact skip exists to prevent.
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("wgA", 0, 0)
                .SRef("nazca", 20000, 0)
            .EndCell()
            .WaveguideCell("wgA")
            .BeginCell("nazca")
                .SRef("ConnectAPIC_NazcaPartial", 0, 0)
            .EndCell()
            .BeginCell("ConnectAPIC_NazcaPartial")
                .Boundary(111, 0, (0, 0), (5000, 0), (5000, 3000), (0, 3000), (0, 0))
            .EndCell()
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        result.ImportedCellDrafts.ShouldHaveSingleItem().CellName.ShouldBe("wgA");
        result.Instances.ShouldHaveSingleItem().CellName.ShouldBe("wgA");
        result.Warnings.ShouldBeEmpty(
            "the wrapped artifact is skipped by convention, not failed as an unpersistable draft");
        var note = result.Infos.ShouldHaveSingleItem();
        note.ShouldContain("nazca");
        note.ShouldContain("ConnectAPIC_NazcaPartial");
        note.ShouldContain("export artifact");
        note.ShouldContain("not reconstructed");
    }

    [Fact]
    public async Task Explode_PassThroughWrapperAroundNormalCell_IsNotSkipped()
    {
        // The wrapper look-through must ONLY recognize wrappers around artifact
        // cells: a pass-through wrapper around a real design cell imports
        // exactly as before (draft named after the wrapper, absorbing the
        // subtree — pins included).
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("nazca", 0, 0)
            .EndCell()
            .BeginCell("nazca")
                .SRef("wgA", 0, 0)
            .EndCell()
            .WaveguideCell("wgA")
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        result.ImportedCellDrafts.ShouldHaveSingleItem().CellName.ShouldBe("nazca");
        result.Instances.ShouldHaveSingleItem().CellName.ShouldBe("nazca");
        result.ImportedCellDrafts[0].Pins.Count.ShouldBe(2);
        result.Warnings.ShouldBeEmpty();
        result.Infos.ShouldBeEmpty("a wrapper around a normal cell is not an export artifact");
    }

    [Fact]
    public async Task Explode_TransformedWrapperAroundArtifact_IsNotSkipped()
    {
        // A wrapper whose reference TRANSFORMS the artifact (here: 90° rotation)
        // is not a pass-through — unwrapping would misplace the geometry, so the
        // cell is treated as ordinary design content (draft, no artifact note).
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("nazca", 0, 0)
            .EndCell()
            .BeginCell("nazca")
                .SRef("ConnectAPIC_NazcaPartial", 0, 0, angleDegrees: 90)
            .EndCell()
            .BeginCell("ConnectAPIC_NazcaPartial")
                .Boundary(111, 0, (0, 0), (5000, 0), (5000, 3000), (0, 3000), (0, 0))
            .EndCell()
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        result.ImportedCellDrafts.ShouldHaveSingleItem().CellName.ShouldBe("nazca");
        result.Instances.ShouldHaveSingleItem().CellName.ShouldBe("nazca");
        result.Infos.ShouldBeEmpty("a transforming wrapper is not a pass-through artifact");
    }

    [Fact]
    public async Task Explode_ZeroGeometryCellResolvingToKnownComponent_IsKept()
    {
        // A deliberate name binding to a PDK component wins over the
        // zero-geometry drop (the size-mismatch warning covers the geometry
        // gap) — only UNKNOWN zero-geometry cells are dropped.
        var known = new KnownComponent(
            "anchor", "testpdk", 5, 5,
            new[] { Pin("io", 0, 0, 180) });

        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("anchor", 0, 0)
            .EndCell()
            .BeginCell("anchor")
                .Text(1, 10, "io", 0, 0)
            .EndCell()
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(
            library, "TOP", new GdsHierarchyImportOptions
            {
                ResolveKnownComponent = name => name == "anchor" ? known : null,
            });

        result.Instances.ShouldHaveSingleItem().KnownComponentIdentifier.ShouldBe("anchor");
        result.Warnings.ShouldContain(w => w.Contains("UNSCALED"),
            "the known component keeps its size-mismatch warning");
        result.Infos.ShouldNotContain(i => i.Contains("skipped"),
            "a resolved zero-geometry cell is not treated as a skip");
    }

    // ── Pin-name normalization (blank/duplicate names) ───────────────────────

    [Fact]
    public async Task Explode_DuplicateAndHeuristicCollidingPinNames_DedupedWithWarnings()
    {
        // Two labels with the same text (legal GDS) plus a label literally
        // named "heur_1" colliding with the heuristic pin: names are made
        // unique BEFORE connection reconstruction, so pin-by-name resolution
        // can never mis-wire. Pin order: left edge by app Y (label y=0,
        // heuristic y=500, label y=1000), then the right-edge label.
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("dup", 0, 0)
            .EndCell()
            .BeginCell("dup")
                .Boundary(1, 0, (0, 1750), (10000, 1750), (10000, 2250), (0, 2250), (0, 1750))
                .Text(1, 10, "o1", 0, 1500)
                .Text(1, 10, "o1", 0, 2500)        // same text twice — legal GDS
                .Text(1, 10, "heur_1", 10000, 2000) // collides with the heuristic pin name
            .EndCell()
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        var draft = result.ImportedCellDrafts.ShouldHaveSingleItem();
        draft.Pins.Select(p => p.Name).ShouldBe(new[] { "o1", "heur_1", "o1_2", "heur_1_2" });
        result.Warnings.ShouldContain(w => w.Contains("duplicate pin name 'o1'") && w.Contains("o1_2"));
        result.Warnings.ShouldContain(w => w.Contains("duplicate pin name 'heur_1'") && w.Contains("heur_1_2"));
    }

    // ── Warning quality ──────────────────────────────────────────────────────

    [Fact]
    public async Task Explode_ArrayWithNonCardinalRotation_WarnsOncePerReferenceWithMemberCount()
    {
        // A 3×3 AREF rotated 45° expands to 9 instances — the snap warning must
        // collapse into ONE per reference (with member count), not flood nine.
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .ARef("wg", columns: 3, rows: 3, originX: 0, originY: 0,
                    columnSpacingDbUnits: 20000, rowSpacingDbUnits: 10000, angleDegrees: 45.0)
            .EndCell()
            .WaveguideCell("wg")
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        result.Instances.Count.ShouldBe(9);
        var rotationWarnings = result.Warnings.Where(w => w.Contains("non-cardinal")).ToList();
        var warning = rotationWarnings.ShouldHaveSingleItem(
            "an AREF must not flood one identical warning per member");
        warning.ShouldContain("9 instances");
        warning.ShouldContain("wg#0");
        warning.ShouldContain("45");
    }

    [Fact]
    public async Task Explode_NegativeMagnification_WarnsAbout180DegreeSnapError()
    {
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("wg", 0, 0, magnification: -2.0)
            .EndCell()
            .WaveguideCell("wg")
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        result.Warnings.ShouldContain(w => w.Contains("NEGATIVE") && w.Contains("180"));
    }

    [Fact]
    public async Task Explode_KnownComponentSizeMismatch_WarnsPinsUnscaledAndConnectionsUncertain()
    {
        // PDK says 30×10 µm but the GDS cell measures 60×10 µm: pins are mapped
        // unscaled onto the GDS bbox, so reconstructed connections may be wrong —
        // the warning must say exactly that.
        var known = new KnownComponent(
            "mmi1x2", "testpdk", 30, 10,
            new[]
            {
                Pin("o1", 0, 5, 180),
                Pin("o2", 30, 5, 0),
            });

        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("mmi1x2", 0, 0)
            .EndCell()
            .BeginCell("mmi1x2")
                .Boundary(1, 0, (0, 0), (60000, 0), (60000, 10000), (0, 10000), (0, 0))
            .EndCell()
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(
            library, "TOP", new GdsHierarchyImportOptions
            {
                ResolveKnownComponent = name => name == "mmi1x2" ? known : null,
            });

        var warning = result.Warnings.First(w => w.Contains("UNSCALED"));
        warning.ShouldContain("geometrically incorrect");
    }

    [Fact]
    public async Task Explode_PinlessDraft_ImporterStaysSilent_ServiceOwnsThatWarning()
    {
        // "blob" has no waveguide-layer geometry and no port labels → no pins.
        // The importer deliberately does NOT warn here: the service reports the
        // more actionable "not registered: no pins" message — two warnings for
        // the same fact would be noise.
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("blob", 0, 0)
            .EndCell()
            .BeginCell("blob")
                .Boundary(111, 0, (0, 0), (10000, 0), (10000, 4000), (0, 4000), (0, 0))
            .EndCell()
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        result.ImportedCellDrafts.ShouldHaveSingleItem().Pins.ShouldBeEmpty();
        result.Warnings.ShouldNotContain(w => w.Contains("no pins"));
    }

    [Fact]
    public async Task Explode_CellNameWithControlCharacters_RawCodeStaysValidPython()
    {
        // A GDS STRING may contain control characters (e.g. a newline) — the
        // emitted Python snippet must not break on them.
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("weird\ncell", 0, 0)
            .EndCell()
            .BeginCell("weird\ncell")
                .Boundary(1, 0, (0, 1750), (10000, 1750), (10000, 2250), (0, 2250), (0, 1750))
                .Text(1, 10, "in", 0, 2000)
                .Text(1, 10, "out", 10000, 2000)
            .EndCell()
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        var draft = result.ImportedCellDrafts.ShouldHaveSingleItem();
        draft.RawCode.ShouldNotContain("weird\ncell");
        draft.RawCode.ShouldContain("weird_cell");
    }

    // ── Fixture helpers ──────────────────────────────────────────────────────

    private static DetectedPin Pin(string name, double x, double y, double angle) =>
        new() { Name = name, XUm = x, YUm = y, AngleDegrees = angle, Source = DetectedPinSource.Label };

    private static async Task<GdsLibrary> ReadLibraryAsync(byte[] gds) =>
        await new GdsReader().ReadAsync(new MemoryStream(gds));

    /// <summary>The PdkLoader rule: pins within [0, W] × [0, H] (±1 µm tolerance).</summary>
    private static void AssertPinsWithinDraftBounds(GdsCellDraft draft)
    {
        foreach (var pin in draft.Pins)
        {
            pin.XUm.ShouldBeGreaterThanOrEqualTo(-1.0);
            pin.XUm.ShouldBeLessThanOrEqualTo(draft.WidthUm + 1.0);
            pin.YUm.ShouldBeGreaterThanOrEqualTo(-1.0);
            pin.YUm.ShouldBeLessThanOrEqualTo(draft.HeightUm + 1.0);
        }
    }
}

/// <summary>GDS fixture cell builders shared by the hierarchy importer tests.</summary>
file static class GdsHierarchyTestCells
{
    /// <summary>
    /// 10×4 µm cell, built like a real gdsfactory waveguide: a 0.5 µm core
    /// stripe (y ∈ [1.75, 2.25]) on the waveguide layer (1,0), an extent
    /// rectangle on the non-waveguide layer (111,0) — it sizes the bbox
    /// without firing the edge heuristic — and in/out port labels on (1,10).
    /// </summary>
    public static GdsTestWriter WaveguideCell(this GdsTestWriter writer, string name, int inY = 2000, int outY = 2000) =>
        writer
            .BeginCell(name)
                .Boundary(1, 0, (0, 1750), (10000, 1750), (10000, 2250), (0, 2250), (0, 1750))
                .Boundary(111, 0, (0, 0), (10000, 0), (10000, 4000), (0, 4000), (0, 0))
                .Text(1, 10, "in", 0, inY)
                .Text(1, 10, "out", 10000, outY)
            .EndCell();
}
